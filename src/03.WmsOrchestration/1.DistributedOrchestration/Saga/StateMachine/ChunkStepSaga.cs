// [담당업무 1] Saga State Machine (3/4) — 청크 하나의 Step 을 순차 실행하고, 실패·부분성공 시 보상 Saga 를 띄운 뒤 끝까지 진행한다.

using MassTransit;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Portfolio.WmsOrchestration.Infrastructure;
using Portfolio.WmsOrchestration.Model;

namespace Portfolio.WmsOrchestration.Saga
{
    /// <summary>
    /// ChunkStep Saga - Sequential Step Execution with Compensation Handling
    /// 
    /// Core Logic:
    /// 1. Processing: Execute all steps sequentially (continue even on FAILED)
    /// 2. WaitingCompensation: Wait for all compensations to complete after ALL steps finish
    /// 3. Completed/Failed: Final state after all steps and compensations complete
    /// 
    /// State Transitions:
    ///   Initial → Processing → WaitingCompensation → Completed / Failed
    ///                       └→ Completed (if no compensation needed)
    /// 
    /// Policy Changes (2026-01-26):
    /// - FAILED status no longer stops step execution
    /// - All steps execute regardless of failures
    /// - Compensations run in parallel with step execution
    /// - Final status determined after ALL steps and compensations complete
    /// </summary>
    public class ChunkStepSaga : MassTransitStateMachine<ChunkStepSagaState>
    {
        #region States

        public State Initialized { get; private set; } = null!;
        public State Processing { get; private set; } = null!;
        public State WaitingCompensation { get; private set; } = null!;
        public State Failed { get; private set; } = null!;
        public State Completed { get; private set; } = null!;

        #endregion

        #region Events

        public Event<ChunkStepInitEvent> StepStart { get; private set; } = null!;
        public Event<ChunkStepResultEvent> StepResult { get; private set; } = null!;
        public Event<CompensationCompleteEvent> CompensationComplete { get; private set; } = null!;
        public Event<CompensationFailEvent> CompensationFail { get; private set; } = null!;

        #endregion

        #region Constants

        private const string EMPTY_JSON_ARRAY = "[]";
        private const string EMPTY_JSON_OBJECT = "{}";
        private const string COMPENSATION_FAILED_ERROR_CODE = "COMPENSATION_FAILED";

        #endregion

        #region Fields

        private readonly ILogger<ChunkStepSaga> _logger;

        #endregion

        #region Constructor

        public ChunkStepSaga(ILogger<ChunkStepSaga> logger)
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
            ConfigureWaitingCompensationState();
            ConfigureTerminalStates();

            SetCompletedWhenFinalized();
        }

        /// <summary>
        /// Configure event correlations by ChunkId
        /// </summary>
        private void ConfigureEvents()
        {
            Event(() => StepStart, cfg => CorrelateByChunkId(cfg));
            Event(() => StepResult, cfg => CorrelateByChunkId(cfg));
            Event(() => CompensationComplete, cfg => CorrelateByChunkId(cfg));
            Event(() => CompensationFail, cfg => CorrelateByChunkId(cfg));
        }

        /// <summary>
        /// Configure initial state: Initialize saga and start first step
        /// </summary>
        private void ConfigureInitialState()
        {
            Initially(
                When(StepStart)
                    .ThenAsync(InitializeSagaAsync)
                    .Activity(x => x.OfType<LoadChunkStepPlanActivity>())
                    .ThenAsync(StartFirstStepAsync)
                    .TransitionTo(Processing)
                    .Catch<Exception>(ex => ex
                        .ThenAsync(HandleInitializationFailureAsync)
                        .TransitionTo(Failed))
            );
        }

        /// <summary>
        /// Configure Processing state: Handle sequential step execution
        /// </summary>
        private void ConfigureProcessingState()
        {
            During(Processing,
                When(StepResult)
                    .Activity(x => x.OfType<UpdateChunkStepResultActivity>())
                    .ThenAsync(HandleStepResultAsync),

                When(StepStart)
                    .Then(context =>
                        _logger.LogWarning(
                            AppLog.Log("[ChunkStepSaga] Duplicate StepStarted ignored. ChunkId={ChunkId}"),
                            context.Message.ChunkId)),

                When(CompensationComplete)
                    .ThenAsync(HandleCompensationCompleteDuringProcessingAsync),

                When(CompensationFail)
                    .ThenAsync(HandleCompensationFailDuringProcessingAsync)
            );
        }

        /// <summary>
        /// Configure WaitingCompensation state: Wait for all compensations to complete
        /// </summary>
        private void ConfigureWaitingCompensationState()
        {
            During(WaitingCompensation,
                When(CompensationComplete)
                    .ThenAsync(HandleCompensationCompleteAsync),

                When(CompensationFail)
                    .ThenAsync(HandleCompensationFailInWaitingAsync),

                Ignore(StepStart),
                Ignore(StepResult)
            );
        }

        /// <summary>
        /// Configure terminal states: Ignore all events in Failed/Completed states
        /// </summary>
        private void ConfigureTerminalStates()
        {
            During(Failed,
                Ignore(StepStart),
                Ignore(StepResult),
                Ignore(CompensationComplete),
                Ignore(CompensationFail)
            );

            During(Completed,
                Ignore(StepStart),
                Ignore(StepResult),
                Ignore(CompensationComplete),
                Ignore(CompensationFail)
            );
        }

        #endregion

        #region Event Handlers - Initialization

        /// <summary>
        /// Initialize saga instance with event data
        /// Sets up initial state and counters
        /// </summary>
        private Task InitializeSagaAsync(
            BehaviorContext<ChunkStepSagaState, ChunkStepInitEvent> context)
        {
            var saga = context.Saga;
            var msg = context.Message;
            var now = DateTime.UtcNow;

            saga.CorrelationId = msg.ChunkId;
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
            saga.CreatedAt = now;
            saga.UpdatedAt = now;
            saga.CurrentStepIndex = 0;
            saga.HasPartialSuccess = false;
            saga.IsStepFailed = false;
            saga.ProcessedItemCount = 0;
            saga.TotalFailCount = 0;
            saga.PendingCompensationsJson = EMPTY_JSON_ARRAY;
            saga.PartialFailureDetailsJson = EMPTY_JSON_OBJECT;

            _logger.LogInformation(
                AppLog.Log("[ChunkStepSaga] Saga initialized. ChunkId={ChunkId}, JobId={JobId}"),
                msg.ChunkId, msg.JobId);

            return Task.CompletedTask;
        }

        /// <summary>
        /// Start execution of the first step
        /// Validates step plan and publishes first step execution event
        /// </summary>
        private async Task StartFirstStepAsync(
            BehaviorContext<ChunkStepSagaState, ChunkStepInitEvent> context)
        {
            var saga = context.Saga;

            if (saga.TotalStepCount == 0)
            {
                _logger.LogError(
                    AppLog.Log("[ChunkStepSaga] No steps defined. ChunkId={ChunkId}"),
                    saga.ChunkId);
                throw new InvalidOperationException($"No steps defined for ChunkId: {saga.ChunkId}");
            }

            var plan = DeserializeStepPlan(saga.StepPlanJson);
            var firstStep = plan[0];

            saga.CurrentStepIndex = 0;
            saga.UpdatedAt = DateTime.UtcNow;

            _logger.LogInformation(
                AppLog.Log("[ChunkStepSaga] Starting first step: ChunkId={ChunkId}, StepType={StepType}, TotalSteps={Total}"),
                saga.ChunkId, firstStep.StepType, saga.TotalStepCount);

            await PublishStepExecutionEventAsync(context, firstStep);
        }

        /// <summary>
        /// Handle saga initialization failure
        /// Transitions to Failed state and publishes failure event
        /// </summary>
        private async Task HandleInitializationFailureAsync(
            BehaviorExceptionContext<ChunkStepSagaState, ChunkStepInitEvent, Exception> context)
        {
            var saga = context.Saga;
            var exception = context.Exception;

            saga.UpdatedAt = DateTime.UtcNow;
            saga.IsStepFailed = true;

            _logger.LogError(exception,
                AppLog.Log("[ChunkStepSaga] Initialization failed. ChunkId={ChunkId}, Error={Error}"),
                saga.ChunkId, exception.Message);

            await PublishChunkResultEventAsync(
                context,
                ChunkScopeStatus.FAILED.ToString(),
                0,
                exception.GetType().Name,
                $"Initialization failed: {exception.Message}",
                exception.StackTrace ?? string.Empty);
        }

        #endregion

        #region Event Handlers - Step Execution

        /// <summary>
        /// Handle step execution result (unified for SUCCESS/PARTIAL_SUCCESS/FAILED)
        /// 
        /// Core Logic (Changed 2026-01-26):
        /// 1. Update processed counts and error details
        /// 2. Route by Status:
        ///    - SUCCESS: Check if last step, if yes complete or continue
        ///    - PARTIAL_SUCCESS: Start compensation, continue to next step
        ///    - FAILED: Start compensation, **CONTINUE to next step** (Policy Change)
        /// 3. ALL steps execute regardless of failures
        /// </summary>
        private async Task HandleStepResultAsync(
            BehaviorContext<ChunkStepSagaState, ChunkStepResultEvent> context)
        {
            var saga = context.Saga;
            var msg = context.Message;

            _logger.LogInformation(
                AppLog.Log("[ChunkStepSaga] Step result received: ChunkId={ChunkId}, StepIndex={StepIndex}, StepType={StepType}, Status={Status}"),
                saga.ChunkId, msg.StepIndex, msg.StepType, msg.Status);

            saga.ProcessedItemCount += msg.ProcessedItemCount;
            saga.UpdatedAt = DateTime.UtcNow;

            // Route by Status
            if (msg.Status == ChunkScopeStatus.COMPLETED.ToString())
            {
                await HandleStepSuccessAsync(context, msg);
            }
            else if (msg.Status == ChunkScopeStatus.PARTIAL_SUCCESS.ToString())
            {
                await HandleStepPartialSuccessAsync(context, msg);
            }
            else if (msg.Status == ChunkScopeStatus.FAILED.ToString())
            {
                await HandleStepFailedAsync(context, msg);
            }
            else
            {
                _logger.LogError(
                    AppLog.Log("[ChunkStepSaga] Unknown step status. ChunkId={ChunkId}, Status={Status}"),
                    saga.ChunkId, msg.Status);
                throw new InvalidOperationException($"Unknown step status: {msg.Status}");
            }
        }

        /// <summary>
        /// Handle step success
        /// Check if last step → complete or continue to next
        /// </summary>
        private async Task HandleStepSuccessAsync(
            BehaviorContext<ChunkStepSagaState, ChunkStepResultEvent> context,
            ChunkStepResultEvent msg)
        {
            var saga = context.Saga;

            _logger.LogInformation(
                AppLog.Log("[ChunkStepSaga] Step completed successfully: ChunkId={ChunkId}, StepIndex={StepIndex}, ProcessedCount={Count}"),
                saga.ChunkId, msg.StepIndex, msg.ProcessedItemCount);

            var nextIndex = msg.StepIndex + 1;

            if (nextIndex >= saga.TotalStepCount)
            {
                await HandleLastStepCompletionAsync(context);
                return;
            }

            await StartNextStepAsync(context, nextIndex);
        }

        /// <summary>
        /// Handle step partial success
        /// Start compensation for failed identifiers and continue to next step
        /// </summary>
        private async Task HandleStepPartialSuccessAsync(
            BehaviorContext<ChunkStepSagaState, ChunkStepResultEvent> context,
            ChunkStepResultEvent msg)
        {
            var saga = context.Saga;

            if (msg.FailedIdentifiers == null || !msg.FailedIdentifiers.Any())
            {
                _logger.LogError(
                    AppLog.Log("[ChunkStepSaga] PARTIAL_SUCCESS without FailedIdentifiers. ChunkId={ChunkId}, StepIndex={StepIndex}"),
                    saga.ChunkId, msg.StepIndex);
                throw new InvalidOperationException("PARTIAL_SUCCESS must have FailedIdentifiers");
            }

            _logger.LogWarning(
                AppLog.Log("[ChunkStepSaga] Partial success detected. ChunkId={ChunkId}, StepIndex={StepIndex}, FailedCount={Count}"),
                saga.ChunkId, msg.StepIndex, msg.FailedIdentifiers.Count);

            saga.HasPartialSuccess = true;
            saga.TotalFailCount += msg.FailedIdentifiers.Count;

            // Store error details for audit
            saga.ErrorCode = msg.ErrorCode;
            saga.ErrorMessage = msg.ErrorMessage;
            saga.ErrorDetail = msg.ErrorDetail;

            AddPartialFailureDetails(saga, msg.StepIndex, msg.FailedIdentifiers);

            var compensationId = await StartCompensationSagaAsync(
                context, msg.StepIndex, msg.FailedIdentifiers);

            if (compensationId != Guid.Empty)
            {
                saga.PendingCompensations.Add(compensationId);
                saga.SyncPendingCompensationsToJson();

                _logger.LogInformation(
                    AppLog.Log("[ChunkStepSaga] Compensation started and tracked: ChunkId={ChunkId}, CompensationId={CompensationId}, PendingCount={Count}"),
                    saga.ChunkId, compensationId, saga.PendingCompensations.Count);
            }

            var nextIndex = msg.StepIndex + 1;

            if (nextIndex >= saga.TotalStepCount)
            {
                await HandleLastStepCompletionAsync(context);
                return;
            }

            await StartNextStepAsync(context, nextIndex);
        }

        /// <summary>
        /// Handle step failure (Business Failure)
        /// 
        /// Core Logic (Changed 2026-01-26):
        /// 1. Mark step as failed and store error details
        /// 2. Start compensation for all previously successful steps
        /// 3. **CONTINUE to next step** (Policy Change - no longer stops execution)
        /// 4. Transition to WaitingCompensation only after ALL steps complete
        /// </summary>
        private async Task HandleStepFailedAsync(
            BehaviorContext<ChunkStepSagaState, ChunkStepResultEvent> context,
            ChunkStepResultEvent msg)
        {
            var saga = context.Saga;

            saga.UpdatedAt = DateTime.UtcNow;
            saga.IsStepFailed = true;
            saga.ErrorCode = msg.ErrorCode;
            saga.ErrorMessage = msg.ErrorMessage;
            saga.ErrorDetail = msg.ErrorDetail;

            _logger.LogError(
                AppLog.Log("[ChunkStepSaga] Step failed (BusinessFail): ChunkId={ChunkId}, StepIndex={StepIndex}, StepType={StepType}, Error={Error}"),
                saga.ChunkId, msg.StepIndex, msg.StepType, msg.ErrorMessage);

            // Start compensation if there are previous steps
            if (msg.StepIndex > 0)
            {
                var compensationId = await StartCompensationSagaForBusinessFailAsync(context, msg.StepIndex);

                if (compensationId != Guid.Empty)
                {
                    saga.PendingCompensations.Add(compensationId);
                    saga.SyncPendingCompensationsToJson();

                    _logger.LogInformation(
                        AppLog.Log("[ChunkStepSaga] Full compensation started (will continue to next step): ChunkId={ChunkId}, CompensationId={CompensationId}"),
                        saga.ChunkId, compensationId);
                }
            }
            else
            {
                _logger.LogInformation(
                    AppLog.Log("[ChunkStepSaga] First step failed. No compensation needed. Continuing to next step: ChunkId={ChunkId}"),
                    saga.ChunkId);
            }

            // Policy Change: Continue to next step regardless of failure
            var nextIndex = msg.StepIndex + 1;

            if (nextIndex >= saga.TotalStepCount)
            {
                await HandleLastStepCompletionAsync(context);
                return;
            }

            _logger.LogInformation(
                AppLog.Log("[ChunkStepSaga] Continuing to next step despite failure: ChunkId={ChunkId}, NextStepIndex={NextIndex}"),
                saga.ChunkId, nextIndex);

            await StartNextStepAsync(context, nextIndex);
        }

        /// <summary>
        /// Handle completion of the last step
        /// Decides whether to wait for compensation or complete immediately
        /// </summary>
        private async Task HandleLastStepCompletionAsync(
            BehaviorContext<ChunkStepSagaState> context)
        {
            var saga = context.Saga;

            if (!saga.PendingCompensations.IsEmpty)
            {
                _logger.LogInformation(
                    AppLog.Log("[ChunkStepSaga] All steps completed. Waiting for {Count} compensations: ChunkId={ChunkId}"),
                    saga.PendingCompensations.Count, saga.ChunkId);

                await context.TransitionToState(WaitingCompensation);
            }
            else
            {
                _logger.LogInformation(
                    AppLog.Log("[ChunkStepSaga] All steps completed with no compensations. Completing chunk: ChunkId={ChunkId}"),
                    saga.ChunkId);

                await CompleteChunkAsync(context);
            }
        }

        /// <summary>
        /// Start execution of the next step
        /// </summary>
        private async Task StartNextStepAsync(
            BehaviorContext<ChunkStepSagaState> context,
            int nextIndex)
        {
            var saga = context.Saga;
            var plan = DeserializeStepPlan(saga.StepPlanJson);
            var nextStep = plan[nextIndex];

            saga.CurrentStepIndex = nextIndex;
            saga.UpdatedAt = DateTime.UtcNow;

            _logger.LogInformation(
                AppLog.Log("[ChunkStepSaga] Starting next step: ChunkId={ChunkId}, StepIndex={StepIndex}, StepType={StepType}"),
                saga.ChunkId, nextIndex, nextStep.StepType);

            await PublishStepExecutionEventAsync(context, nextStep);
        }

        #endregion

        #region Event Handlers - Compensation

        /// <summary>
        /// Handle compensation completion during Processing state
        /// Only tracks completion - saga continues processing steps
        /// </summary>
        private Task HandleCompensationCompleteDuringProcessingAsync(
            BehaviorContext<ChunkStepSagaState, CompensationCompleteEvent> context)
        {
            var saga = context.Saga;
            var msg = context.Message;

            saga.PendingCompensations.Remove(msg.CompensationId);
            saga.SyncPendingCompensationsToJson();
            saga.UpdatedAt = DateTime.UtcNow;

            _logger.LogInformation(
                AppLog.Log("[ChunkStepSaga] Compensation completed during processing: ChunkId={ChunkId}, CompensationId={CompensationId}, RemainingCompensations={Count}"),
                saga.ChunkId, msg.CompensationId, saga.PendingCompensations.Count);

            return Task.CompletedTask;
        }

        /// <summary>
        /// Handle compensation failure during Processing state
        /// Log and track, but continue processing steps (Policy: always continue)
        /// </summary>
        private Task HandleCompensationFailDuringProcessingAsync(
            BehaviorContext<ChunkStepSagaState, CompensationFailEvent> context)
        {
            var saga = context.Saga;
            var msg = context.Message;

            saga.PendingCompensations.Remove(msg.CompensationId);
            saga.SyncPendingCompensationsToJson();
            saga.UpdatedAt = DateTime.UtcNow;

            _logger.LogError(
                AppLog.Log("[ChunkStepSaga] Compensation failed during processing (continuing): ChunkId={ChunkId}, CompensationId={CompensationId}, Error={Error}"),
                saga.ChunkId, msg.CompensationId, msg.ErrorMessage);

            // Publish alert but don't stop processing
            return context.Publish(new CompensationFailAlertEvent
            {
                JobId = saga.JobId,
                DomainType = saga.DomainType,
                TotalChunkCount = saga.TotalChunkCount,
                Delimiter = saga.Delimiter,
                UserId = saga.UserId,
                ChunkId = saga.ChunkId,
                Identifier = saga.Identifier,
                ChunkIndex = saga.ChunkIndex,
                CompensationId = msg.CompensationId,
                FailedStepIndex = msg.FailedStepIndex,
                FailedStepType = msg.FailedStepType,
                FailedIdentifiers = msg.FailedIdentifiers,
                ErrorCode = msg.ErrorCode,
                ErrorMessage = msg.ErrorMessage,
                ErrorDetail = msg.ErrorDetail,
                FailedAt = msg.FailedAt,
                CompensatedStepIndexes = msg.CompensatedStepIndexes,
                SkippedStepIndexes = msg.SkippedStepIndexes
            });
        }

        /// <summary>
        /// Handle compensation completion in WaitingCompensation state
        /// 
        /// Core Logic:
        /// 1. Remove completed compensation from pending list
        /// 2. Check if all compensations are complete
        ///    - If yes → Finalize chunk (determine final status)
        ///    - If no → Continue waiting
        /// </summary>
        private async Task HandleCompensationCompleteAsync(
            BehaviorContext<ChunkStepSagaState, CompensationCompleteEvent> context)
        {
            var saga = context.Saga;
            var msg = context.Message;

            saga.PendingCompensations.Remove(msg.CompensationId);
            saga.SyncPendingCompensationsToJson();
            saga.UpdatedAt = DateTime.UtcNow;

            _logger.LogInformation(
                AppLog.Log("[ChunkStepSaga] Compensation completed: ChunkId={ChunkId}, CompensationId={CompensationId}, RemainingCompensations={Count}"),
                saga.ChunkId, msg.CompensationId, saga.PendingCompensations.Count);

            if (saga.PendingCompensations.IsEmpty)
            {
                _logger.LogInformation(
                    AppLog.Log("[ChunkStepSaga] All compensations completed: ChunkId={ChunkId}"),
                    saga.ChunkId);

                saga.CompletedAt = DateTime.UtcNow;
                saga.UpdatedAt = DateTime.UtcNow;

                await CompleteChunkAsync(context);
            }
            else
            {
                _logger.LogDebug(
                    AppLog.Log("[ChunkStepSaga] Still waiting for {Count} compensations: ChunkId={ChunkId}"),
                    saga.PendingCompensations.Count, saga.ChunkId);
            }
        }

        /// <summary>
        /// Handle compensation failure in WaitingCompensation state
        /// Log alert but continue waiting for other compensations (Policy: always continue)
        /// </summary>
        private async Task HandleCompensationFailInWaitingAsync(
            BehaviorContext<ChunkStepSagaState, CompensationFailEvent> context)
        {
            var saga = context.Saga;
            var msg = context.Message;

            saga.PendingCompensations.Remove(msg.CompensationId);
            saga.SyncPendingCompensationsToJson();
            saga.UpdatedAt = DateTime.UtcNow;

            _logger.LogError(
                AppLog.Log("[ChunkStepSaga] Compensation failed in waiting state: ChunkId={ChunkId}, CompensationId={CompensationId}, Error={Error}"),
                saga.ChunkId, msg.CompensationId, msg.ErrorMessage);

            // Publish alert
            await context.Publish(new CompensationFailAlertEvent
            {
                JobId = saga.JobId,
                DomainType = saga.DomainType,
                TotalChunkCount = saga.TotalChunkCount,
                Delimiter = saga.Delimiter,
                UserId = saga.UserId,
                ChunkId = saga.ChunkId,
                Identifier = saga.Identifier,
                ChunkIndex = saga.ChunkIndex,
                CompensationId = msg.CompensationId,
                FailedStepIndex = msg.FailedStepIndex,
                FailedStepType = msg.FailedStepType,
                FailedIdentifiers = msg.FailedIdentifiers,
                ErrorCode = msg.ErrorCode,
                ErrorMessage = msg.ErrorMessage,
                ErrorDetail = msg.ErrorDetail,
                FailedAt = msg.FailedAt,
                CompensatedStepIndexes = msg.CompensatedStepIndexes,
                SkippedStepIndexes = msg.SkippedStepIndexes
            });

            // Check if all compensations are done
            if (saga.PendingCompensations.IsEmpty)
            {
                _logger.LogInformation(
                    AppLog.Log("[ChunkStepSaga] All compensations completed (some failed): ChunkId={ChunkId}"),
                    saga.ChunkId);

                saga.CompletedAt = DateTime.UtcNow;
                saga.UpdatedAt = DateTime.UtcNow;

                await CompleteChunkAsync(context);
            }
        }

        #endregion

        #region Compensation Saga Management

        /// <summary>
        /// Start compensation saga for partial success scenario
        /// Compensates only failed identifiers from previous steps
        /// </summary>
        private async Task<Guid> StartCompensationSagaAsync(
            BehaviorContext<ChunkStepSagaState> context,
            int currentStepIndex,
            List<string> failedIdentifiers)
        {
            var saga = context.Saga;

            var compensationSteps = GetCompensationSteps(saga, currentStepIndex);

            if (!compensationSteps.Any())
            {
                _logger.LogDebug(
                    AppLog.Log("[ChunkStepSaga] No previous steps to compensate. ChunkId={ChunkId}"),
                    saga.ChunkId);
                return Guid.Empty;
            }

            var compensationId = Guid.NewGuid();
            var evt = new ChunkStepCompensationInitEvent();

            evt.MapFromSagaStateForCompensation(
                saga,
                compensationId,
                JsonConvert.SerializeObject(compensationSteps),
                failedIdentifiers);

            await context.Publish(evt, x => x.CorrelationId = compensationId);

            _logger.LogInformation(
                AppLog.Log("[ChunkStepSaga] CompensationSaga started (PartialSuccess). ChunkId={ChunkId}, CompensationId={CompensationId}, StepsToCompensate={Count}, FailedIdentifiers={FailedCount}"),
                saga.ChunkId, compensationId, compensationSteps.Count, failedIdentifiers.Count);

            return compensationId;
        }

        /// <summary>
        /// Start compensation saga for business failure scenario
        /// Compensates all items from all previous successful steps
        /// </summary>
        private async Task<Guid> StartCompensationSagaForBusinessFailAsync(
            BehaviorContext<ChunkStepSagaState> context,
            int failedStepIndex)
        {
            var saga = context.Saga;

            var compensationSteps = GetCompensationSteps(saga, failedStepIndex);

            if (!compensationSteps.Any())
            {
                _logger.LogDebug(
                    AppLog.Log("[ChunkStepSaga] No previous steps to compensate. ChunkId={ChunkId}"),
                    saga.ChunkId);
                return Guid.Empty;
            }

            var compensationId = IdentifierGenerator.GetSequentialGUID();
            var evt = new ChunkStepCompensationInitEvent();

            // In BusinessFail, compensate ALL items from previous steps
            // so we pass an empty list for TargetIdentifiers
            evt.MapFromSagaStateForCompensation(
                saga,
                compensationId,
                JsonConvert.SerializeObject(compensationSteps),
                new List<string>());

            await context.Publish(evt, x => x.CorrelationId = compensationId);

            _logger.LogWarning(
                AppLog.Log("[ChunkStepSaga] CompensationSaga started (BusinessFail). ChunkId={ChunkId}, CompensationId={CompensationId}, StepsToCompensate={Count}"),
                saga.ChunkId, compensationId, compensationSteps.Count);

            return compensationId;
        }

        /// <summary>
        /// Get list of steps that need compensation
        /// Returns steps in reverse order (most recent first)
        /// </summary>
        private List<CompensationStepInfo> GetCompensationSteps(
            ChunkStepSagaState saga,
            int beforeStepIndex)
        {
            var plan = DeserializeStepPlan(saga.StepPlanJson);

            return plan
                .Where(s => s.Index < beforeStepIndex)
                .OrderByDescending(s => s.Index)
                .Select(s => new CompensationStepInfo
                {
                    StepIndex = s.Index,
                    StepType = s.StepType
                })
                .ToList();
        }

        #endregion

        #region Event Publishing

        /// <summary>
        /// Publish step execution event using reflection
        /// Creates domain-specific event type and publishes it
        /// </summary>
        private async Task PublishStepExecutionEventAsync(
            BehaviorContext<ChunkStepSagaState> context,
            ChunkStepPlanInfo step)
        {
            var saga = context.Saga;

            var evt = DomainEventFactory.CreateDomainEvent<ChunkStepDomainEventBase>(
                step.StepType,
                _logger,
                $"ChunkId={saga.ChunkId}");

            evt.MapFromSagaState(saga, step);

            await context.Publish(
                evt,
                evt.GetType(),
                x => x.CorrelationId = saga.ChunkId);

            _logger.LogDebug(
                AppLog.Log("[ChunkStepSaga] Published step execution event: ChunkId={ChunkId}, StepType={StepType}"),
                saga.ChunkId, step.StepType);
        }

        /// <summary>
        /// Publish chunk result event to JobSaga
        /// </summary>
        private Task PublishChunkResultEventAsync(
            BehaviorContext<ChunkStepSagaState> context,
            string status,
            int processedItemCount,
            string? errorCode = null,
            string? errorMessage = null,
            string? errorDetail = null)
        {
            var saga = context.Saga;

            var evt = new ChunkResultEvent
            {
                JobId = saga.JobId,
                DomainType = saga.DomainType,
                TotalChunkCount = saga.TotalChunkCount,
                Delimiter = saga.Delimiter ?? string.Empty,
                UserId = saga.UserId,
                ChunkId = saga.ChunkId,
                Identifier = saga.Identifier,
                ChunkIndex = saga.ChunkIndex,
                Status = status,
                ProcessedItemCount = processedItemCount,
                StartedAt = saga.CreatedAt,
                CompletedAt = DateTime.UtcNow,
                HandlerName = nameof(ChunkStepSaga),
                Message = BuildStatusMessage(saga, status),
                RetryCount = 0,
                IsRetryable = false,
                ErrorCode = errorCode,
                ErrorMessage = errorMessage,
                ErrorDetail = errorDetail
            };

            _logger.LogInformation(
                AppLog.Log("[ChunkStepSaga] Publishing chunk result event: ChunkId={ChunkId}, Status={Status}, ProcessedCount={Count}"),
                saga.ChunkId, status, processedItemCount);

            return context.Publish(evt, x => x.CorrelationId = saga.JobId);
        }

        #endregion

        #region Helper Methods - Completion

        /// <summary>
        /// Complete chunk and determine final status
        /// 
        /// Final Status Logic:
        /// - IsStepFailed = true → FAILED
        /// - HasPartialSuccess = true → PARTIAL_SUCCESS
        /// - Otherwise → COMPLETED
        /// </summary>
        private async Task CompleteChunkAsync(
            BehaviorContext<ChunkStepSagaState> context)
        {
            var saga = context.Saga;

            _logger.LogInformation(
                AppLog.Log("[ChunkStepSaga] Finalizing chunk: ChunkId={ChunkId}, IsStepFailed={IsFailed}, HasPartialSuccess={HasPartial}"),
                saga.ChunkId, saga.IsStepFailed, saga.HasPartialSuccess);

            string finalStatus;
            State finalState;

            if (saga.IsStepFailed)
            {
                finalStatus = ChunkScopeStatus.FAILED.ToString();
                finalState = Failed;
            }
            else if (saga.HasPartialSuccess)
            {
                finalStatus = ChunkScopeStatus.PARTIAL_SUCCESS.ToString();
                finalState = Completed;
            }
            else
            {
                finalStatus = ChunkScopeStatus.COMPLETED.ToString();
                finalState = Completed;
            }

            await PublishChunkResultEventAsync(
                context,
                finalStatus,
                saga.ProcessedItemCount,
                saga.ErrorCode,
                saga.ErrorMessage,
                saga.ErrorDetail);

            await context.TransitionToState(finalState);

            saga.CompletedAt = DateTime.UtcNow;
            saga.UpdatedAt = DateTime.UtcNow;
        }

        #endregion

        #region Helper Methods - Partial Failure Tracking

        /// <summary>
        /// Add partial failure details for audit trail
        /// Tracks which identifiers failed at which step
        /// </summary>
        private void AddPartialFailureDetails(
            ChunkStepSagaState saga,
            int stepIndex,
            List<string> failedIdentifiers)
        {
            try
            {
                var details = JsonConvert.DeserializeObject<Dictionary<int, List<string>>>(
                    saga.PartialFailureDetailsJson) ?? new Dictionary<int, List<string>>();

                if (details.ContainsKey(stepIndex))
                {
                    var existing = details[stepIndex];
                    foreach (var id in failedIdentifiers)
                    {
                        if (!existing.Contains(id))
                        {
                            existing.Add(id);
                        }
                    }
                }
                else
                {
                    details[stepIndex] = failedIdentifiers.ToList();
                }

                saga.PartialFailureDetailsJson = JsonConvert.SerializeObject(details);

                _logger.LogDebug(
                    AppLog.Log("[ChunkStepSaga] Added partial failure details: ChunkId={ChunkId}, StepIndex={StepIndex}, FailedCount={Count}"),
                    saga.ChunkId, stepIndex, failedIdentifiers.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    AppLog.Log("[ChunkStepSaga] Failed to add partial failure details. ChunkId={ChunkId}, StepIndex={StepIndex}"),
                    saga.ChunkId, stepIndex);
            }
        }

        /// <summary>
        /// Build status message based on saga state
        /// </summary>
        private string BuildStatusMessage(ChunkStepSagaState saga, string status)
        {
            if (status == ChunkScopeStatus.COMPLETED.ToString())
            {
                return $"Chunk completed successfully. Success={saga.ProcessedItemCount}";
            }
            else if (status == ChunkScopeStatus.PARTIAL_SUCCESS.ToString())
            {
                return $"Chunk completed with partial success. Success={saga.ProcessedItemCount}, Fail={saga.TotalFailCount}";
            }
            else
            {
                return $"Chunk failed. Processed={saga.ProcessedItemCount}";
            }
        }

        #endregion

        #region Helper Methods - Deserialization

        /// <summary>
        /// Deserialize step plan JSON
        /// </summary>
        private List<ChunkStepPlanInfo> DeserializeStepPlan(string stepPlanJson)
        {
            try
            {
                var plan = JsonConvert.DeserializeObject<List<ChunkStepPlanInfo>>(stepPlanJson);

                if (plan == null || !plan.Any())
                {
                    throw new InvalidOperationException("Step plan is null or empty");
                }

                return plan;
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex,
                    AppLog.Log("[ChunkStepSaga] Failed to deserialize step plan"));
                throw new InvalidOperationException("Invalid step plan JSON format", ex);
            }
        }

        #endregion

        #region Helper Methods - Correlation

        /// <summary>
        /// Configure event correlation by ChunkId
        /// </summary>
        private void CorrelateByChunkId<T>(
            IEventCorrelationConfigurator<ChunkStepSagaState, T> cfg)
            where T : class, IChunkScope
        {
            cfg.CorrelateById(m => m.Message.ChunkId);
        }

        #endregion
    }
}