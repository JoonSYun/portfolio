// [담당업무 1] Saga State Machine (4/4) — 실패한 Step 이전의 Step 들을 역순으로 하나씩 보상한다.

using MassTransit;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Portfolio.WmsOrchestration.Infrastructure;

namespace Portfolio.WmsOrchestration.Saga
{
    /// <summary>
    /// ChunkStep Compensation Saga - Sequential Compensation Execution
    /// 
    /// Core Logic:
    /// 1. Execute compensation steps in reverse order, one by one
    /// 2. Wait for each step completion before starting next (order guarantee)
    /// 3. Publish alert and terminate on compensation failure
    /// 
    /// State Transitions:
    ///   Initial → Compensating → Completed / Failed
    /// </summary>
    public class ChunkStepCompensationSaga : MassTransitStateMachine<ChunkStepCompensationSagaState>
    {
        #region States

        public State Compensating { get; private set; } = null!;
        public State Completed { get; private set; } = null!;
        public State Failed { get; private set; } = null!;

        #endregion

        #region Events

        public Event<ChunkStepCompensationInitEvent> CompensationStart { get; private set; } = null!;
        public Event<ChunkStepCompensationCompleteEvent> CompensationStepComplete { get; private set; } = null!;
        public Event<ChunkStepCompensationFailEvent> CompensationStepCompleteFail { get; private set; } = null!;

        #endregion

        #region Fields

        private readonly ILogger<ChunkStepCompensationSaga> _logger;

        #endregion

        #region Constructor

        public ChunkStepCompensationSaga(ILogger<ChunkStepCompensationSaga> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            ConfigureStateMachine();
        }

        #endregion

        #region State Machine Configuration

        /// <summary>
        /// Configure state machine transitions and event handlers
        /// </summary>
        private void ConfigureStateMachine()
        {
            InstanceState(x => x.CurrentState);

            ConfigureEvents();
            ConfigureInitialState();
            ConfigureCompensatingState();
            ConfigureTerminalStates();

            SetCompletedWhenFinalized();
        }

        /// <summary>
        /// Configure event correlations by CompensationId
        /// </summary>
        private void ConfigureEvents()
        {
            Event(() => CompensationStart, cfg => CorrelateByCompensationId(cfg));
            Event(() => CompensationStepComplete, cfg => CorrelateByCompensationId(cfg));
            Event(() => CompensationStepCompleteFail, cfg => CorrelateByCompensationId(cfg));
        }

        /// <summary>
        /// Configure initial state: Initialize saga and start first compensation
        /// </summary>
        private void ConfigureInitialState()
        {
            Initially(
                When(CompensationStart)
                    .ThenAsync(InitializeSagaAsync)
                    .ThenAsync(StartFirstCompensationAsync)
                    .TransitionTo(Compensating)
                    .Catch<Exception>(ex => ex
                        .ThenAsync(HandleInitializationFailureAsync)
                        .TransitionTo(Failed)
                        .Finalize())
            );
        }

        /// <summary>
        /// Configure Compensating state: Handle sequential compensation execution
        /// </summary>
        private void ConfigureCompensatingState()
        {
            During(Compensating,
                When(CompensationStepComplete)
                    .ThenAsync(HandleStepCompletedAsync),

                When(CompensationStepCompleteFail)
                    .ThenAsync(HandleStepFailedAsync)
                    .TransitionTo(Failed)
                    .Finalize(),

                When(CompensationStart)
                    .Then(context =>
                        _logger.LogWarning(
                            AppLog.Log("[CompensationSaga] Duplicate CompensationStarted ignored. CompensationId={CompensationId}"),
                            context.Message.CompensationId))
            );
        }

        /// <summary>
        /// Configure terminal states: Ignore all events in Failed/Completed states
        /// </summary>
        private void ConfigureTerminalStates()
        {
            During(Failed,
                Ignore(CompensationStart),
                Ignore(CompensationStepComplete),
                Ignore(CompensationStepCompleteFail)
            );

            During(Completed,
                Ignore(CompensationStart),
                Ignore(CompensationStepComplete),
                Ignore(CompensationStepCompleteFail)
            );
        }

        #endregion

        #region Event Handlers - Initialization

        /// <summary>
        /// Initialize saga instance with event data
        /// </summary>
        private Task InitializeSagaAsync(
            BehaviorContext<ChunkStepCompensationSagaState, ChunkStepCompensationInitEvent> context)
        {
            var saga = context.Saga;
            var msg = context.Message;

            saga.CorrelationId = msg.CompensationId;
            saga.CompensationId = msg.CompensationId;
            saga.JobId = msg.JobId;
            saga.ChunkId = msg.ChunkId;
            saga.Identifier = msg.Identifier;
            saga.ChunkIndex = msg.ChunkIndex;
            saga.DomainType = msg.DomainType;
            saga.TotalChunkCount = msg.TotalChunkCount;
            saga.Delimiter = msg.Delimiter;
            saga.RequestBrandCode = msg.RequestBrandCode;
            saga.SlipDiv = msg.SlipDiv;
            saga.UserId = msg.UserId;
            saga.CompensationPlanJson = msg.CompensationPlanJson;
            saga.FailedIdentifiersJson = JsonConvert.SerializeObject(msg.FailedIdentifiers);
            saga.PayloadJson = msg.PayloadJson;
            saga.CreatedAt = DateTime.UtcNow;
            saga.UpdatedAt = DateTime.UtcNow;
            saga.CurrentCompensationIndex = 0;
            saga.CompensatedStepCount = 0;
            saga.CompensatedStepIndexesJson = "[]";

            var plan = JsonConvert.DeserializeObject<List<CompensationStepInfo>>(msg.CompensationPlanJson)!;
            saga.TotalCompensationSteps = plan.Count;

            _logger.LogInformation(
                AppLog.Log("[CompensationSaga] Saga initialized. CompensationId={CompensationId}, ChunkId={ChunkId}, TotalSteps={TotalSteps}, FailedIdentifiers={FailCount}"),
                msg.CompensationId, msg.ChunkId, saga.TotalCompensationSteps, msg.FailedIdentifiers.Count);

            return Task.CompletedTask;
        }

        /// <summary>
        /// Start first compensation step
        /// </summary>
        private async Task StartFirstCompensationAsync(
            BehaviorContext<ChunkStepCompensationSagaState, ChunkStepCompensationInitEvent> context)
        {
            var saga = context.Saga;

            if (saga.TotalCompensationSteps == 0)
            {
                _logger.LogWarning(
                    AppLog.Log("[CompensationSaga] No steps to compensate. Completing immediately. CompensationId={CompensationId}"),
                    saga.CompensationId);

                await PublishCompensationCompleteEventAsync(context);
                await context.TransitionToState(Completed);
                saga.CompletedAt = DateTime.UtcNow;
                return;
            }

            var plan = JsonConvert.DeserializeObject<List<CompensationStepInfo>>(saga.CompensationPlanJson)!;
            var first = plan[0];

            saga.CurrentCompensationIndex = 0;
            saga.UpdatedAt = DateTime.UtcNow;

            _logger.LogInformation(
                AppLog.Log("[CompensationSaga] Starting first compensation step: CompensationId={CompensationId}, StepIndex={StepIndex}, StepType={StepType}"),
                saga.CompensationId, first.StepIndex, first.StepType);

            await PublishCompensationStepEventAsync(context, first);
        }

        /// <summary>
        /// Handle initialization failure
        /// </summary>
        private async Task HandleInitializationFailureAsync(
            BehaviorExceptionContext<ChunkStepCompensationSagaState, ChunkStepCompensationInitEvent, Exception> context)
        {
            var saga = context.Saga;
            var exception = context.Exception;

            saga.ErrorCode = exception.GetType().Name;
            saga.ErrorMessage = exception.Message;
            saga.ErrorDetail = exception.StackTrace;
            saga.FailedAt = DateTime.UtcNow;
            saga.UpdatedAt = DateTime.UtcNow;

            _logger.LogError(exception,
                AppLog.Log("[CompensationSaga] Initialization failed. CompensationId={CompensationId}, Error={Error}"),
                saga.CompensationId, exception.Message);

            await PublishCompensationFailedAlertAsync(context, -1, "INITIALIZATION", exception);
        }

        #endregion

        #region Event Handlers - Compensation Execution

        /// <summary>
        /// ⭐ Handle compensation step completion (Core Logic)
        /// 
        /// Logic:
        /// 1. Record completion
        /// 2. All steps complete → Saga complete
        /// 3. Next step exists → Start next step (sequential guarantee)
        /// </summary>
        private async Task HandleStepCompletedAsync(
            BehaviorContext<ChunkStepCompensationSagaState, ChunkStepCompensationCompleteEvent> context)
        {
            var saga = context.Saga;
            var msg = context.Message;

            _logger.LogInformation(
                AppLog.Log("[CompensationSaga] Compensation step completed: CompensationId={CompensationId}, StepIndex={StepIndex}, StepType={StepType}, CompensatedCount={Count}"),
                saga.CompensationId, msg.StepIndex, msg.StepType, msg.CompensatedCount);

            // 1. Record completion
            var compensatedIndexes = JsonConvert.DeserializeObject<List<int>>(saga.CompensatedStepIndexesJson) ?? new List<int>();
            compensatedIndexes.Add(msg.StepIndex);
            saga.CompensatedStepIndexesJson = JsonConvert.SerializeObject(compensatedIndexes);
            saga.CompensatedStepCount++;
            saga.UpdatedAt = DateTime.UtcNow;

            var nextIndex = saga.CurrentCompensationIndex + 1;

            // 2. All compensation complete
            if (nextIndex >= saga.TotalCompensationSteps)
            {
                _logger.LogInformation(
                    AppLog.Log("[CompensationSaga] All compensation steps completed: CompensationId={CompensationId}, TotalSteps={TotalSteps}"),
                    saga.CompensationId, saga.TotalCompensationSteps);

                await PublishCompensationCompleteEventAsync(context);
                await context.TransitionToState(Completed);
                saga.CompletedAt = DateTime.UtcNow;
                saga.UpdatedAt = DateTime.UtcNow;
                return;
            }

            // 3. ⭐ Start next compensation step (sequential guarantee)
            var plan = JsonConvert.DeserializeObject<List<CompensationStepInfo>>(saga.CompensationPlanJson)!;
            var nextStep = plan[nextIndex];

            saga.CurrentCompensationIndex = nextIndex;
            saga.UpdatedAt = DateTime.UtcNow;

            _logger.LogInformation(
                AppLog.Log("[CompensationSaga] Starting next compensation step: CompensationId={CompensationId}, StepIndex={StepIndex}, StepType={StepType}"),
                saga.CompensationId, nextStep.StepIndex, nextStep.StepType);

            await PublishCompensationStepEventAsync(context, nextStep);
        }

        /// <summary>
        /// ⭐ Handle compensation step failure
        /// 
        /// Logic:
        /// 1. Record failure information
        /// 2. Publish alert (developer intervention required)
        /// 3. Terminate saga (skip remaining compensations)
        /// </summary>
        private async Task HandleStepFailedAsync(
            BehaviorContext<ChunkStepCompensationSagaState, ChunkStepCompensationFailEvent> context)
        {
            var saga = context.Saga;
            var msg = context.Message;

            saga.FailedStepIndex = msg.StepIndex;
            saga.FailedStepType = msg.StepType;
            saga.ErrorCode = msg.ErrorCode;
            saga.ErrorMessage = msg.ErrorMessage;
            saga.ErrorDetail = msg.ErrorDetail;
            saga.FailedAt = msg.FailedAt;
            saga.UpdatedAt = DateTime.UtcNow;

            _logger.LogError(
                AppLog.Log("[CompensationSaga] Compensation step failed: CompensationId={CompensationId}, StepIndex={StepIndex}, StepType={StepType}, Error={Error}"),
                saga.CompensationId, msg.StepIndex, msg.StepType, msg.ErrorMessage);

            await PublishCompensationFailedAlertAsync(context, msg.StepIndex, msg.StepType, null);
        }

        #endregion

        #region Event Publishing

        /// <summary>
        /// Publish compensation step event using reflection
        /// Correlation: CorrelationId = CompensationId (for Consumer response routing)
        /// </summary>
        private async Task PublishCompensationStepEventAsync(
            BehaviorContext<ChunkStepCompensationSagaState> context,
            CompensationStepInfo step)
        {
            var saga = context.Saga;
            var failedIdentifiers = JsonConvert.DeserializeObject<List<string>>(saga.FailedIdentifiersJson) ?? new List<string>();

            // ⭐ DomainEventFactory 사용
            var evt = DomainEventFactory.CreateDomainEvent<ChunkStepDomainEventBase>(
                step.StepType,
                _logger,
                $"CompensationId={saga.CompensationId}");

            // ⭐ EventMappingExtensions 사용 (보상 모드)
            evt.MapFromCompensationSagaState(saga, step, failedIdentifiers);

            // Publish with CompensationId correlation (for Consumer response routing back to this saga)
            await context.Publish(
                evt,
                evt.GetType(),
                x => x.CorrelationId = saga.CompensationId);

            _logger.LogDebug(
                AppLog.Log("[CompensationSaga] Published compensation step event: CompensationId={CompensationId}, StepIndex={StepIndex}, StepType={StepType}, TargetCount={Count}"),
                saga.CompensationId, step.StepIndex, step.StepType, failedIdentifiers.Count);
        }

        /// <summary>
        /// Publish compensation complete event
        /// Correlation: CorrelationId = ChunkId (for ChunkStepSaga routing)
        /// </summary>
        private Task PublishCompensationCompleteEventAsync(
            BehaviorContext<ChunkStepCompensationSagaState> context)
        {
            var saga = context.Saga;

            var evt = new CompensationCompleteEvent
            {
                CompensationId = saga.CompensationId,
                JobId = saga.JobId,
                ChunkId = saga.ChunkId,
                Identifier = saga.Identifier,
                ChunkIndex = saga.ChunkIndex,
                DomainType = saga.DomainType,
                TotalChunkCount = saga.TotalChunkCount,
                Delimiter = saga.Delimiter ?? string.Empty,
                UserId = saga.UserId,
                TotalCompensatedSteps = saga.CompensatedStepCount,
                CompletedAt = DateTime.UtcNow,
                Message = $"All compensation steps completed. Total={saga.CompensatedStepCount}"
            };

            _logger.LogInformation(
                AppLog.Log("[CompensationSaga] Publishing compensation complete event: CompensationId={CompensationId}, TotalSteps={TotalSteps}"),
                saga.CompensationId, saga.CompensatedStepCount);

            // Correlate with ChunkId for ChunkStepSaga routing
            return context.Publish(evt, x => x.CorrelationId = saga.ChunkId);
        }

        /// <summary>
        /// Publish compensation failed alert
        /// Correlation: CorrelationId = ChunkId (for ChunkStepSaga routing)
        /// </summary>
        private Task PublishCompensationFailedAlertAsync(
            BehaviorContext<ChunkStepCompensationSagaState> context,
            int failedStepIndex,
            string failedStepType,
            Exception? exception)
        {
            var saga = context.Saga;

            var compensatedIndexes = JsonConvert.DeserializeObject<List<int>>(saga.CompensatedStepIndexesJson) ?? new List<int>();
            var plan = JsonConvert.DeserializeObject<List<CompensationStepInfo>>(saga.CompensationPlanJson) ?? new List<CompensationStepInfo>();

            var skippedIndexes = plan
                .Skip(saga.CurrentCompensationIndex + 1)
                .Select(s => s.StepIndex)
                .ToList();

            var failedIdentifiers = JsonConvert.DeserializeObject<List<string>>(saga.FailedIdentifiersJson) ?? new List<string>();

            var evt = new CompensationFailEvent
            {
                CompensationId = saga.CompensationId,
                JobId = saga.JobId,
                ChunkId = saga.ChunkId,
                Identifier = saga.Identifier,
                ChunkIndex = saga.ChunkIndex,
                DomainType = saga.DomainType,
                TotalChunkCount = saga.TotalChunkCount,
                Delimiter = saga.Delimiter ?? string.Empty,
                UserId = saga.UserId,
                FailedStepIndex = failedStepIndex,
                FailedStepType = failedStepType,
                FailedIdentifiers = failedIdentifiers,
                ErrorCode = saga.ErrorCode ?? exception?.GetType().Name,
                ErrorMessage = saga.ErrorMessage ?? exception?.Message,
                ErrorDetail = saga.ErrorDetail ?? exception?.StackTrace,
                FailedAt = DateTime.UtcNow,
                CompensatedStepIndexes = compensatedIndexes,
                SkippedStepIndexes = skippedIndexes
            };

            _logger.LogError(
                AppLog.Log("[CompensationSaga] Publishing compensation failed alert: CompensationId={CompensationId}, FailedStep={StepIndex}, Compensated={Compensated}, Skipped={Skipped}"),
                saga.CompensationId, failedStepIndex, compensatedIndexes.Count, skippedIndexes.Count);

            // Correlate with ChunkId for ChunkStepSaga routing
            return context.Publish(evt, x => x.CorrelationId = saga.ChunkId);
        }

        #endregion

        #region Helper Methods - Correlation

        /// <summary>
        /// Configure event correlation by CompensationId
        /// </summary>
        private void CorrelateByCompensationId<T>(
            IEventCorrelationConfigurator<ChunkStepCompensationSagaState, T> cfg)
            where T : class, ICompensationScope
        {
            cfg.CorrelateById(m => m.Message.CompensationId);
        }

        #endregion
    }
}