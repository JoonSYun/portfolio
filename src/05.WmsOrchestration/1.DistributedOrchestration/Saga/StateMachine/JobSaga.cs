// [담당업무 1] Saga State Machine (1/4) — Job 수준. 병렬 청크 결과를 집계하되 Saga 상태를 직접 갱신하지 않고 DB 갱신 커맨드를 발행한다.

using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Portfolio.WmsOrchestration.Infrastructure;
using Portfolio.WmsOrchestration.Model;

namespace Portfolio.WmsOrchestration.Saga
{
    /// <summary>
    /// Job Saga - Job-level Orchestration and State Aggregation
    /// 
    /// ⚠️ CRITICAL DESIGN NOTE: DB-based State Updates (Not Saga State Updates)
    /// 
    /// ═══════════════════════════════════════════════════════════════════════
    /// WHY JobSaga uses DB updates instead of Saga state updates:
    /// ═══════════════════════════════════════════════════════════════════════
    /// 
    /// Problem:
    ///   - Multiple Chunks execute in PARALLEL for the same JobId
    ///   - Each Chunk publishes ChunkResultEvent simultaneously
    ///   - If JobSaga updates its own state directly → CONCURRENCY CONFLICT
    ///   - MassTransit Saga uses Optimistic Concurrency Control (Version field)
    ///   - Concurrent state updates → Version mismatch → Update failure
    /// 
    /// Example Scenario:
    ///   JobId: JOB-001 with 10 Chunks (Chunk-1 ~ Chunk-10)
    ///   
    ///   Time 0ms:  Chunk-1 completes → ChunkResultEvent → JobSaga (Version=1)
    ///   Time 5ms:  Chunk-2 completes → ChunkResultEvent → JobSaga (Version=1)
    ///   Time 10ms: Chunk-3 completes → ChunkResultEvent → JobSaga (Version=1)
    ///   
    ///   If JobSaga updates state directly:
    ///     - Chunk-1 update: Version 1→2 ✅ Success
    ///     - Chunk-2 update: Version 1→2 ❌ CONFLICT (expected Version=2)
    ///     - Chunk-3 update: Version 1→2 ❌ CONFLICT (expected Version=2)
    ///   
    ///   Result: Lost updates, incorrect aggregation
    /// 
    /// Solution:
    ///   JobSaga publishes UPDATE COMMANDS instead of updating its own state.
    ///   A separate Consumer (ChunkResultUpdateCommandConsumer) receives these commands
    ///   and updates the ChunkMaster table using DB-level concurrency control.
    ///   
    ///   Flow:
    ///     ChunkResultEvent → JobSaga (aggregate logic only)
    ///                      → Publish ChunkResultUpdateCommand
    ///                      → ChunkResultUpdateCommandConsumer
    ///                      → DB UPDATE (with proper locking/transaction)
    ///   
    ///   Benefit:
    ///     - JobSaga focuses on orchestration only (lightweight)
    ///     - DB handles concurrent updates properly (pessimistic locking, transactions)
    ///     - No Saga version conflicts
    ///     - Better scalability for high-concurrency scenarios
    /// 
    /// State Transitions:
    ///   Initial → Processing → ChunkCompleted → Completed
    ///   
    /// States:
    ///   - Processing: Receiving Chunk events, publishing update commands
    ///   - ChunkCompleted: All Chunks complete, waiting for JobProcessSaga
    ///   - Completed: Job fully completed after JobProcessSaga finishes
    /// 
    /// ═══════════════════════════════════════════════════════════════════════
    /// </summary>
    public class JobSaga : MassTransitStateMachine<JobSagaState>
    {
        #region States

        public State Processing { get; private set; } = null!;
        public State ChunkCompleted { get; private set; } = null!;
        public State Completed { get; private set; } = null!;

        #endregion

        #region Events

        public Event<JobStartEvent> JobStart { get; private set; } = null!;
        public Event<ChunkResultEvent> ChunkResult { get; private set; } = null!;
        public Event<JobSuspendedEvent> JobSuspended { get; private set; } = null!;
        public Event<AllChunkCompleteEvent> AllChunkComplete { get; private set; } = null!;
        public Event<AllJobProcessCompleteEvent> AllJobProcessComplete { get; private set; } = null!;

        #endregion

        #region Constants

        private const int DEFAULT_MAX_RETRY_COUNT = 3;

        #endregion

        #region Fields

        private readonly ILogger<JobSaga> _logger;
        private readonly int _maxRetryCount;

        #endregion

        #region Constructor

        public JobSaga(
            ILogger<JobSaga> logger,
            IConfiguration configuration)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _maxRetryCount = configuration.GetValue<int>("MaxRetryCount", DEFAULT_MAX_RETRY_COUNT);

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
            ConfigureChunkCompletedState();
            ConfigureCompletedState();

            SetCompletedWhenFinalized();
        }

        /// <summary>
        /// Configure event correlations by JobId
        /// </summary>
        private void ConfigureEvents()
        {
            Event(() => JobStart, cfg => CorrelateByJobId(cfg));
            Event(() => ChunkResult, cfg => CorrelateByJobId(cfg));
            Event(() => JobSuspended, cfg => CorrelateByJobId(cfg));
            Event(() => AllChunkComplete, cfg => CorrelateByJobId(cfg));
            Event(() => AllJobProcessComplete, cfg => CorrelateByJobId(cfg));
        }

        /// <summary>
        /// Configure initial state: Initialize saga on JobStarted
        /// </summary>
        private void ConfigureInitialState()
        {
            Initially(
                When(JobStart)
                    .ThenAsync(HandleJobStartedAsync)
                    .TransitionTo(Processing)
            );
        }

        /// <summary>
        /// Configure Processing state: Receive and process Chunk events
        /// Publishes update commands to DB Consumer
        /// </summary>
        private void ConfigureProcessingState()
        {
            During(Processing,
                When(ChunkResult)
                    .ThenAsync(HandleChunkResultAsync),

                When(AllChunkComplete)
                    .ThenAsync(HandleAllChunkCompleteAsync)
                    .TransitionTo(ChunkCompleted),

                When(JobSuspended)
                    .ThenAsync(HandleJobSuspendedAsync),

                When(JobStart)
                    .Then(context =>
                        _logger.LogWarning(
                            AppLog.Log("[JobSaga] Duplicate JobStarted ignored. JobId={JobId}"),
                            context.Message.JobId))
            );
        }

        /// <summary>
        /// Configure ChunkCompleted state: Wait for JobProcessSaga completion
        /// </summary>
        private void ConfigureChunkCompletedState()
        {
            During(ChunkCompleted,
                When(AllJobProcessComplete)
                    .ThenAsync(HandleJobProcessCompletedAsync)
                    .TransitionTo(Completed),

                Ignore(ChunkResult),
                Ignore(AllChunkComplete),
                Ignore(JobStart),
                Ignore(JobSuspended)
            );
        }

        /// <summary>
        /// Configure Completed state: Ignore all events
        /// </summary>
        private void ConfigureCompletedState()
        {
            During(Completed,
                Ignore(ChunkResult),
                Ignore(AllChunkComplete),
                Ignore(JobStart),
                Ignore(JobSuspended),
                Ignore(AllJobProcessComplete)
            );
        }

        #endregion

        #region Event Handlers - Job Lifecycle

        /// <summary>
        /// Handle job start event
        /// Initialize saga state and optionally publish JobPublishCommand
        /// </summary>
        private async Task HandleJobStartedAsync(
            BehaviorContext<JobSagaState, JobStartEvent> context)
        {
            var saga = context.Saga;
            var msg = context.Message;

            _logger.LogInformation(
                AppLog.Log("[JobSaga] Job started. JobId={JobId}, DomainType={DomainType}, TotalChunks={TotalChunks}"),
                msg.JobId, msg.DomainType, msg.TotalChunkCount);

            saga.CorrelationId = msg.JobId;
            saga.JobIdentifier = msg.JobIdentifier;
            saga.JobIdentifierType = msg.JobIdentifierType;
            saga.DomainType = msg.DomainType;
            saga.TotalChunkCount = msg.TotalChunkCount;
            saga.Delimiter = msg.Delimiter;
            saga.RequestBrandCode = msg.RequestBrandCode;
            saga.SlipDiv = msg.SlipDiv;
            saga.UserId = msg.UserId;
            saga.SignalR_YN = msg.SignalR_YN;
            saga.IsBypass = msg.IsBypass;
            saga.ChunkStatus = ChunkScopeStatus.PENDING.ToString();
            saga.JobProcessStatus = JobScopeStatus.PENDING.ToString();
            saga.CreatedAt = DateTime.UtcNow;

            // Bypass requests are processed asynchronously,
            // using the same execution flow as standard batch async jobs.
            var cmd = new JobPublishCommand
            {
                JobId = msg.JobId,
                JobIdentifier = msg.JobIdentifier,
                DomainType = msg.DomainType,
                TotalChunkCount = msg.TotalChunkCount,
                Delimiter = msg.Delimiter,
                UserId = msg.UserId,
                Priority = msg.Priority,
                RequestBrandCode = msg.RequestBrandCode,
                SlipDiv = msg.SlipDiv
            };

            await context.Publish(cmd, x => x.CorrelationId = msg.JobId);

            _logger.LogDebug(
                AppLog.Log("[JobSaga] Published JobPublishCommand. JobId={JobId}"),
                msg.JobId);
        }

        /// <summary>
        /// Handle job suspension event
        /// Publishes suspension update command to DB
        /// </summary>
        private async Task HandleJobSuspendedAsync(
            BehaviorContext<JobSagaState, JobSuspendedEvent> context)
        {
            var saga = context.Saga;
            var msg = context.Message;

            _logger.LogWarning(
                AppLog.Log("[JobSaga] Job suspended. JobId={JobId}, SuspenseType={SuspenseType}"),
                msg.JobId, msg.SuspenseType);

            saga.SuspendedAt = DateTime.UtcNow;

            var cmd = new JobSuspendedUpdateCommand
            {
                JobId = msg.JobId,
                JobIdentifier = msg.JobIdentifier,
                DomainType = saga.DomainType,
                TotalChunkCount = saga.TotalChunkCount,
                Delimiter = saga.Delimiter,
                UserId = saga.UserId,
                IdempotencyKey = msg.IdempotencyKey,
                SuspenseType = msg.SuspenseType,
                CreatedBy = msg.CreatedBy,
                SuspenseReason = msg.SuspenseReason
            };

            await context.Publish(cmd, x => x.CorrelationId = msg.JobId);
        }

        #endregion

        #region Event Handlers - Chunk Events

        /// <summary>
        /// Handle chunk result event (unified for COMPLETED/PARTIAL_SUCCESS/FAILED)
        /// 
        /// CRITICAL: Does NOT update Saga state directly
        /// Publishes update command to ChunkResultUpdateCommandConsumer for DB update
        /// 
        /// Reason: Parallel Chunks → Concurrent events → DB handles concurrency
        /// ChunkStepSaga determines the final Status → JobSaga trusts it
        /// </summary>
        private async Task HandleChunkResultAsync(
            BehaviorContext<JobSagaState, ChunkResultEvent> context)
        {
            var saga = context.Saga;
            var msg = context.Message;

            _logger.LogInformation(
                AppLog.Log("[JobSaga] Chunk result received. JobId={JobId}, ChunkId={ChunkId}, Status={Status}"),
                msg.JobId, msg.ChunkId, msg.Status);

            var cmd = new ChunkResultUpdateCommand
            {
                JobId = msg.JobId,
                DomainType = saga.DomainType,
                TotalChunkCount = saga.TotalChunkCount,
                Delimiter = saga.Delimiter,
                UserId = saga.UserId,
                ChunkId = msg.ChunkId,
                Identifier = msg.Identifier,
                ChunkIndex = msg.ChunkIndex,
                Status = msg.Status,
                ProcessedItemCount = msg.ProcessedItemCount,
                StartedAt = msg.StartedAt,
                CompletedAt = msg.CompletedAt,
                HandlerName = msg.HandlerName,
                Message = msg.Message,
                RetryCount = msg.RetryCount,
                IsRetryable = msg.IsRetryable,
                ErrorCode = msg.ErrorCode,
                ErrorMessage = msg.ErrorMessage,
                ErrorDetail = msg.ErrorDetail
            };

            await context.Publish(cmd, x => x.CorrelationId = cmd.JobId);
        }

        /// <summary>
        /// Handle all chunks complete event
        /// Update saga state with chunk results and start JobProcessSaga
        /// </summary>
        private async Task HandleAllChunkCompleteAsync(
            BehaviorContext<JobSagaState, AllChunkCompleteEvent> context)
        {
            var saga = context.Saga;
            var msg = context.Message;

            _logger.LogInformation(
                AppLog.Log("[JobSaga] All chunks completed. Starting JobProcessSaga. JobId={JobId}, TotalChunks={TotalChunks}"),
                msg.JobId, msg.TotalChunkCount);

            // Update saga state with chunk aggregation results
            saga.ChunkStatus = msg.Status;
            saga.ChunkSuccessCount = msg.ChunkResult?.SuccessCount ?? 0;
            saga.ChunkPartialSuccessCount = msg.ChunkResult?.PartialSuccessCount ?? 0;
            saga.ChunkFailedCount = msg.ChunkResult?.FailedCount ?? 0;

            // Start JobProcessSaga
            var evt = new JobProcessInitEvent
            {
                JobId = msg.JobId,
                JobIdentifier = saga.JobIdentifier,
                DomainType = saga.DomainType,
                TotalChunkCount = saga.TotalChunkCount,
                JobStatus = msg.Status,
                Delimiter = saga.Delimiter ?? string.Empty,
                RequestBrandCode = saga.RequestBrandCode,
                SlipDiv = saga.SlipDiv,
                UserId = saga.UserId
            };

            await context.Publish(evt, x => x.CorrelationId = msg.JobId);

            _logger.LogInformation(
                AppLog.Log("[JobSaga] Published JobProcessInitEvent. JobId={JobId}"),
                msg.JobId);
        }

        /// <summary>
        /// Handle job process completed event
        /// Update saga state and publish final job complete command
        /// 
        /// ⭐ Final Status Logic:
        ///   - JobProcess COMPLETED → Use ChunkStatus (COMPLETED/PARTIAL_SUCCESS/FAILED)
        ///   - JobProcess FAILED → Use JobProcessStatus (FAILED)
        /// </summary>
        private async Task HandleJobProcessCompletedAsync(
            BehaviorContext<JobSagaState, AllJobProcessCompleteEvent> context)
        {
            var saga = context.Saga;
            var msg = context.Message;

            saga.CompletedAt = DateTime.UtcNow;
            saga.JobProcessStatus = msg.Status;

            _logger.LogInformation(
                AppLog.Log("[JobSaga] JobProcess completed. Finalizing job. JobId={JobId}, Status={Status}"),
                msg.JobId, msg.Status);

            // Determine final status
            string finalStatus;
            if (saga.JobProcessStatus == JobScopeStatus.COMPLETED.ToString())
            {
                // JobProcess succeeded → Use Chunk aggregation result
                finalStatus = saga.ChunkStatus!;
            }
            else
            {
                // JobProcess failed → Job is FAILED
                finalStatus = saga.JobProcessStatus!;
            }

            var cmd = new JobCompleteUpdateCommand
            {
                JobId = msg.JobId,
                JobIdentifier = saga.JobIdentifier,
                DomainType = saga.DomainType,
                TotalChunkCount = saga.TotalChunkCount,
                Delimiter = saga.Delimiter,
                RequestBrandCode = saga.RequestBrandCode,
                SlipDiv = saga.SlipDiv,
                UserId = saga.UserId,
                Status = finalStatus,
                IsBypass = saga.IsBypass,
                ChunkSuccessCount = saga.ChunkSuccessCount,
                ChunkPartialSuccessCount = saga.ChunkPartialSuccessCount,
                ChunkFailedCount = saga.ChunkFailedCount
            };

            await context.Publish(cmd, x => x.CorrelationId = msg.JobId);

            _logger.LogInformation(
                AppLog.Log("[JobSaga] Published JobCompleteUpdateCommand. JobId={JobId}, FinalStatus={Status}"),
                msg.JobId, finalStatus);
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Configure event correlation by JobId
        /// </summary>
        private void CorrelateByJobId<T>(
            IEventCorrelationConfigurator<JobSagaState, T> cfg)
            where T : class, IJobSagaEvent
        {
            cfg.CorrelateById(m => m.Message.JobId);
        }

        #endregion
    }
}