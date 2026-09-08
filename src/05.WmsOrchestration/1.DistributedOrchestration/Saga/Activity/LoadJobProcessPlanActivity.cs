using MassTransit;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Portfolio.WmsOrchestration.Repository;
using Portfolio.WmsOrchestration.Infrastructure;
using System.Text;
using Portfolio.WmsOrchestration.Model;
using Portfolio.WmsOrchestration.Exceptions;

namespace Portfolio.WmsOrchestration.Saga
{
    /// <summary>
    /// Load job process plan from DB (data loading only)
    /// Business logic handled in saga
    /// </summary>
    public class LoadJobProcessPlanActivity
        : IStateMachineActivity<JobProcessSagaState, JobProcessInitEvent>
    {
        private readonly ILogger<LoadJobProcessPlanActivity> _logger;
        private readonly JobProcessQueryRepository _repo;
        private readonly ChunkPayloadQueryRepository _chunkPayloadQueryRepository;
        private readonly OrchestrationRethriveRetryPolicyFactory _retryPolicyFactory;

        public LoadJobProcessPlanActivity(
            ILogger<LoadJobProcessPlanActivity> logger,
            JobProcessQueryRepository repo,
            ChunkPayloadQueryRepository chunkPayloadQueryRepository,
            OrchestrationRethriveRetryPolicyFactory retryPolicyFactory
            )
        {
            _logger = logger;
            _repo = repo;
            _chunkPayloadQueryRepository = chunkPayloadQueryRepository;
            _retryPolicyFactory = retryPolicyFactory;
        }

        public void Accept(StateMachineVisitor visitor)
        {
            visitor.Visit(this);
        }

        public async Task Execute(
            BehaviorContext<JobProcessSagaState, JobProcessInitEvent> context,
            IBehavior<JobProcessSagaState, JobProcessInitEvent> next)
        {
            var saga = context.Saga;
            var msg = context.Message;

            var policy = _retryPolicyFactory.CreateRetryPolicy();

            // 병렬 조회 + Retry 적용
            var (processes, payload) = await policy.ExecuteAsync(async () =>
            {
                var processesTask = _repo.GetProcessesByJobIdAsync(msg.JobId);
                var payloadTask = _chunkPayloadQueryRepository.GetPayloadsByJobIdAsync(msg.JobId);

                await Task.WhenAll(processesTask, payloadTask);

                return (await processesTask, await payloadTask);
            });

            if (payload == null || payload.Count == 0)
            {
                _logger.LogError(
                    AppLog.Log("[LoadJobProcessPlanActivity] No payload found for JobId={JobId}"),
                    msg.JobId);
                throw new InvalidOperationException($"No payload data found for the job : {msg.JobId}");
            }

            var plan = processes
                .OrderBy(p => p.ProcessIndex)
                .Select(p => new ProcessPlanInfo
                {
                    Index = p.ProcessIndex,
                    ProcessId = p.ProcessId,
                    ProcessType = p.ProcessType,
                    IdempotencyKey = p.IdempotencyKey
                })
                .ToList();

            saga.TotalProcessCount = plan.Count;
            saga.ProcessPlanJson = JsonConvert.SerializeObject(plan);

            // Payloads 저장
            // 이때 처음에 List<string>으로 변환 된 데이터를 변환 한다는 점 주의
            List<string> payloadStrings = payload.Select(r => Encoding.UTF8.GetString(r.Payload)).ToList();
            saga.PayloadJson = JsonConvert.SerializeObject(payloadStrings);

            _logger.LogInformation(
                AppLog.Log("[LoadJobProcessPlanActivity] Loaded plan. JobId={JobId}, ProcessCount={Count}"),
                saga.CorrelationId, saga.TotalProcessCount);

            await next.Execute(context);
        }

        public Task Faulted<TException>(
            BehaviorExceptionContext<JobProcessSagaState, JobProcessInitEvent, TException> context,
            IBehavior<JobProcessSagaState, JobProcessInitEvent> next)
            where TException : Exception
            => next.Faulted(context);

        public void Probe(ProbeContext context)
        {
            context.CreateScope("load-job-process-plan");
        }
    }
}