// [담당업무 1] Saga State Machine (2/4) — 모든 청크가 끝난 뒤 Job 후처리(Process)를 순차 실행한다.

using MassTransit;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Portfolio.WmsOrchestration.Infrastructure;
using Portfolio.WmsOrchestration.Model;

namespace Portfolio.WmsOrchestration.Saga
{
    /// <summary>
    /// JobProcess Saga - Sequential Process Execution per Job
    /// 
    /// Pattern:
    ///   - Identical structure to ChunkStepSaga
    ///   - Sequential execution based on ProcessPlan
    ///   - ProcessType → Type resolution → Reflection-based publish
    /// 
    /// State Transitions:
    ///   Initial → Processing → Completed / Failed
    ///   Initial → Completed (when no processes defined)
    /// </summary>
    public class JobProcessSaga : MassTransitStateMachine<JobProcessSagaState>
    {
        #region States

        public State Processing { get; private set; } = null!;
        public State Failed { get; private set; } = null!;
        public State Completed { get; private set; } = null!;

        #endregion

        #region Events

        public Event<JobProcessInitEvent> ProcessStart { get; private set; } = null!;
        public Event<ProcessCompleteEvent> ProcessComplete { get; private set; } = null!;
        public Event<ProcessFailEvent> ProcessFail { get; private set; } = null!;

        #endregion

        #region Fields

        private readonly ILogger<JobProcessSaga> _logger;

        #endregion

        #region Constructor

        public JobProcessSaga(ILogger<JobProcessSaga> logger)
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
            ConfigureProcessingState();
            ConfigureTerminalStates();

            SetCompletedWhenFinalized();
        }

        /// <summary>
        /// Configure event correlations by JobId
        /// </summary>
        private void ConfigureEvents()
        {
            Event(() => ProcessStart, cfg => CorrelateByJobId(cfg));
            Event(() => ProcessComplete, cfg => CorrelateByJobId(cfg));
            Event(() => ProcessFail, cfg => CorrelateByJobId(cfg));
        }

        /// <summary>
        /// Configure initial state: Initialize saga and start first process
        /// Handles no-process case by completing immediately
        /// </summary>
        private void ConfigureInitialState()
        {
            Initially(
                When(ProcessStart)
                    .ThenAsync(InitializeSagaAsync)
                    .Activity(x => x.OfType<LoadJobProcessPlanActivity>())
                    .IfElseAsync(
                        // Condition: Check if there are no processes to execute
                        context => Task.FromResult(context.Saga.TotalProcessCount == 0),
                        // Then: No processes defined → complete immediately
                        noProcess => noProcess
                            .ThenAsync(HandleNoProcessAsync)
                            .TransitionTo(Completed)
                            .Finalize(),
                        // Else: Start first process
                        hasProcess => hasProcess
                            .ThenAsync(StartFirstProcessAsync)
                            .TransitionTo(Processing)
                    )
                    .Catch<Exception>(ex => ex
                        .ThenAsync(HandleInitializationFailureAsync)
                        .TransitionTo(Failed)
                        .Finalize())
            );
        }

        /// <summary>
        /// Configure Processing state: Handle sequential process execution
        /// </summary>
        private void ConfigureProcessingState()
        {
            During(Processing,
                When(ProcessComplete)
                    .Activity(x => x.OfType<UpdateJobProcessSuccessActivity>())
                    .ThenAsync(ExecuteNextProcessAsync),

                When(ProcessFail)
                    .Activity(x => x.OfType<UpdateJobProcessFailActivity>())
                    .ThenAsync(HandleProcessFailAsync)
                    .TransitionTo(Failed)
                    .Finalize(),

                When(ProcessStart)
                    .Then(context =>
                        _logger.LogWarning(
                            AppLog.Log("[JobProcessSaga] Duplicate ProcessStarted ignored. JobId={JobId}"),
                            context.Message.JobId))
            );
        }

        /// <summary>
        /// Configure terminal states: Ignore all events in Failed/Completed states
        /// </summary>
        private void ConfigureTerminalStates()
        {
            During(Failed,
                Ignore(ProcessStart),
                Ignore(ProcessComplete),
                Ignore(ProcessFail)
            );

            During(Completed,
                Ignore(ProcessStart),
                Ignore(ProcessComplete),
                Ignore(ProcessFail)
            );
        }

        #endregion

        #region Event Handlers - Initialization

        /// <summary>
        /// Initialize saga instance and persist to DB
        /// </summary>
        private Task InitializeSagaAsync(
            BehaviorContext<JobProcessSagaState, JobProcessInitEvent> context)
        {
            var saga = context.Saga;
            var msg = context.Message;

            saga.CorrelationId = msg.JobId;
            saga.JobIdentifier = msg.JobIdentifier;
            saga.DomainType = msg.DomainType;
            saga.TotalChunkCount = msg.TotalChunkCount;
            saga.JobStatus = msg.JobStatus;
            saga.Delimiter = msg.Delimiter;
            saga.RequestBrandCode = msg.RequestBrandCode;
            saga.SlipDiv = msg.SlipDiv;
            saga.UserId = msg.UserId;
            saga.CreatedAt = DateTime.UtcNow;
            saga.UpdatedAt = DateTime.UtcNow;
            saga.CurrentProcessIndex = 0;

            _logger.LogInformation(
                AppLog.Log("[JobProcessSaga] Saga initialized and saved to DB. JobId={JobId}"),
                msg.JobId);

            return Task.CompletedTask;
        }

        /// <summary>
        /// Handle case when no processes defined - complete job immediately
        /// </summary>
        private async Task HandleNoProcessAsync(
            BehaviorContext<JobProcessSagaState, JobProcessInitEvent> context)
        {
            var saga = context.Saga;

            saga.CompletedAt = DateTime.UtcNow;
            saga.UpdatedAt = DateTime.UtcNow;

            _logger.LogInformation(
                AppLog.Log("[JobProcessSaga] No processes defined. Completing job immediately. JobId={JobId}"),
                saga.CorrelationId);

            await PublishJobProcessCompleteEventAsync(context, true);
        }

        /// <summary>
        /// Start first process execution
        /// </summary>
        private async Task StartFirstProcessAsync(
            BehaviorContext<JobProcessSagaState, JobProcessInitEvent> context)
        {
            var saga = context.Saga;

            var plan = DeserializeProcessPlan(saga.ProcessPlanJson);
            var first = plan[0];

            saga.CurrentProcessIndex = 0;
            saga.UpdatedAt = DateTime.UtcNow;

            _logger.LogInformation(
                AppLog.Log("[JobProcessSaga] Starting first process: JobId={JobId}, ProcessType={ProcessType}, TotalProcesses={TotalProcesses}"),
                saga.CorrelationId, first.ProcessType, saga.TotalProcessCount);

            await PublishProcessExecutionEventAsync(context, first);
        }

        /// <summary>
        /// Handle initialization failure and publish complete event with failed status
        /// </summary>
        private async Task HandleInitializationFailureAsync(
            BehaviorExceptionContext<JobProcessSagaState, JobProcessInitEvent, Exception> context)
        {
            var saga = context.Saga;
            var exception = context.Exception;

            saga.FailedProcessIndex = 0;
            saga.FailedProcessCount = 1;
            saga.UpdatedAt = DateTime.UtcNow;

            _logger.LogError(exception,
                AppLog.Log("[JobProcessSaga] Initialization failed. JobId={JobId}, Error={Error}"),
                saga.CorrelationId, exception.Message);

            await PublishJobProcessCompleteEventAsync(context, false);
        }

        #endregion

        #region Event Handlers - Process Execution

        /// <summary>
        /// Execute next process or complete job processing
        /// </summary>
        private async Task ExecuteNextProcessAsync(
            BehaviorContext<JobProcessSagaState, ProcessCompleteEvent> context)
        {
            var saga = context.Saga;
            var msg = context.Message;

            _logger.LogInformation(
                AppLog.Log("[JobProcessSaga] Process completed: JobId={JobId}, ProcessIndex={ProcessIndex}, ProcessType={ProcessType}"),
                saga.CorrelationId, msg.ProcessIndex, msg.ProcessType);

            var executed = JsonConvert.DeserializeObject<List<int>>(saga.ExecutedProcessesJson)
                           ?? new List<int>();

            executed.Add(msg.ProcessIndex);
            saga.ExecutedProcessesJson = JsonConvert.SerializeObject(executed);
            saga.UpdatedAt = DateTime.UtcNow;

            var nextIndex = msg.ProcessIndex + 1;

            if (nextIndex >= saga.TotalProcessCount)
            {
                _logger.LogInformation(
                    AppLog.Log("[JobProcessSaga] All processes completed: JobId={JobId}, TotalProcesses={TotalProcesses}"),
                    saga.CorrelationId, saga.TotalProcessCount);

                await PublishJobProcessCompleteEventAsync(context, true);
                await context.TransitionToState(Completed);
                saga.CompletedAt = DateTime.UtcNow;
                saga.UpdatedAt = DateTime.UtcNow;
                return;
            }

            var plan = DeserializeProcessPlan(saga.ProcessPlanJson);
            var nextProcess = plan[nextIndex];

            saga.CurrentProcessIndex = nextIndex;
            saga.UpdatedAt = DateTime.UtcNow;

            _logger.LogInformation(
                AppLog.Log("[JobProcessSaga] Starting next process: JobId={JobId}, ProcessIndex={ProcessIndex}, ProcessType={ProcessType}"),
                saga.CorrelationId, nextIndex, nextProcess.ProcessType);

            await PublishProcessExecutionEventAsync(context, nextProcess);
        }

        /// <summary>
        /// Handle process failure and publish complete event with failed status
        /// </summary>
        private async Task HandleProcessFailAsync(
            BehaviorContext<JobProcessSagaState, ProcessFailEvent> context)
        {
            var saga = context.Saga;
            var msg = context.Message;

            saga.FailedProcessIndex = msg.ProcessIndex;
            saga.FailedProcessCount++;
            saga.UpdatedAt = DateTime.UtcNow;

            _logger.LogError(
                AppLog.Log("[JobProcessSaga] Process failed: JobId={JobId}, ProcessIndex={ProcessIndex}, ProcessType={ProcessType}, Error={Error}"),
                saga.CorrelationId, msg.ProcessIndex, msg.ProcessType, msg.ErrorMessage);

            await PublishJobProcessCompleteEventAsync(context, false);
        }

        #endregion

        #region Event Publishing

        /// <summary>
        /// Publish process execution event using reflection
        /// Key pattern: ProcessType (DTO type string) → Type resolution → Reflection-based publish
        /// Same approach as ChunkStepSaga
        /// </summary>
        private async Task PublishProcessExecutionEventAsync(
            BehaviorContext<JobProcessSagaState> context,
            ProcessPlanInfo process)
        {
            var saga = context.Saga;

            // ⭐ DomainEventFactory 사용
            var evt = DomainEventFactory.CreateDomainEvent<JobProcessDomainEventBase>(
                process.ProcessType,
                _logger,
                $"JobId={saga.CorrelationId}");

            // ⭐ EventMappingExtensions 사용
            evt.MapFromSagaState(saga, process);

            await context.Publish(
                evt,
                evt.GetType(),
                x => x.CorrelationId = saga.CorrelationId);

            _logger.LogDebug(
                AppLog.Log("[JobProcessSaga] Published process execution event: JobId={JobId}, ProcessType={ProcessType}"),
                saga.CorrelationId, process.ProcessType);
        }

        /// <summary>
        /// Publish job process complete event (success or failed based on saga state)
        /// </summary>
        private Task PublishJobProcessCompleteEventAsync(
            BehaviorContext<JobProcessSagaState> context,
            bool allSuccess)
        {
            var saga = context.Saga;

            var status = saga.FailedProcessCount == 0
                ? saga.JobStatus
                : JobScopeStatus.FAILED.ToString();

            var evt = new AllJobProcessCompleteEvent
            {
                JobId = saga.CorrelationId,
                DomainType = saga.DomainType,
                TotalChunkCount = saga.TotalChunkCount,
                Delimiter = saga.Delimiter ?? string.Empty,
                UserId = saga.UserId,
                TotalProcessCount = saga.TotalProcessCount,
                AllSuccess = allSuccess,
                FailedProcessIndex = saga.FailedProcessIndex,
                Status = status
            };

            _logger.LogInformation(
                AppLog.Log("[JobProcessSaga] Publishing job process complete event: JobId={JobId}, Status={Status}"),
                saga.CorrelationId, evt.Status);

            return context.Publish(evt, x => x.CorrelationId = saga.CorrelationId);
        }

        #endregion

        #region Helper Methods - Deserialization

        /// <summary>
        /// Deserialize process plan JSON
        /// </summary>
        private List<ProcessPlanInfo> DeserializeProcessPlan(string processPlanJson)
        {
            try
            {
                var plan = JsonConvert.DeserializeObject<List<ProcessPlanInfo>>(processPlanJson);

                if (plan == null || !plan.Any())
                {
                    throw new InvalidOperationException("Process plan is null or empty");
                }

                return plan;
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex,
                    AppLog.Log("[JobProcessSaga] Failed to deserialize process plan"));
                throw new InvalidOperationException("Invalid process plan JSON format", ex);
            }
        }

        #endregion

        #region Helper Methods - Correlation

        /// <summary>
        /// Configure event correlation by JobId
        /// </summary>
        private void CorrelateByJobId<T>(
            IEventCorrelationConfigurator<JobProcessSagaState, T> cfg)
            where T : class, IJobScope
        {
            cfg.CorrelateById(m => m.Message.JobId);
        }

        #endregion
    }
}