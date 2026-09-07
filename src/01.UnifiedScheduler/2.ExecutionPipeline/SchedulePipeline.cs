using Cronos;
using Hangfire;
using Portfolio.UnifiedScheduler.ConfigurationInheritance;
using Portfolio.UnifiedScheduler.FaultIsolationAndOperations.OperationalControl;

namespace Portfolio.UnifiedScheduler.ExecutionPipeline;

/// <summary>
/// [담당업무 2] 8계층 파이프라인의 1~5단계 (Register → Sync → Fire → Filter → Enqueue).
/// 6~8단계(Execute → Finalize → Notify)는 큐에서 꺼낸 워커가 <see cref="JobBase"/> 로 수행한다.
///
/// 설계 포인트: 브랜드 fan-out 을 "발사(Fire)" 단계에 둔다.
/// 등록(Register)은 도메인당 RecurringJob 하나 → 등록 부담은 도메인 수(75)로 유지되고,
/// 실행만 브랜드(34) 단위로 분리된다. 6개 → 75개 도메인 확장이 배포 없이 가능했던 이유.
/// </summary>
public sealed class SchedulePipeline
{
    private readonly IScheduleConfigRepository _config;
    private readonly ScheduleConfigResolver _resolver;
    private readonly IOperationalGate _gate;
    private readonly IRecurringJobManager _recurring;
    private readonly IBackgroundJobClient _jobs;
    private readonly ILogger<SchedulePipeline> _log;

    public SchedulePipeline(IScheduleConfigRepository config, ScheduleConfigResolver resolver, IOperationalGate gate,
        IRecurringJobManager recurring, IBackgroundJobClient jobs, ILogger<SchedulePipeline> log)
    {
        _config = config; _resolver = resolver; _gate = gate; _recurring = recurring; _jobs = jobs; _log = log;
    }

    // ── 1. Register ───────────────────────────────────────────────────────────
    /// <summary>도메인마다 RecurringJob 하나. Cron 유효성은 Cronos 로 등록 시점에 검증한다.</summary>
    public async Task RegisterAllAsync(CancellationToken ct)
    {
        foreach (var domain in await _config.GetEnabledDomainsAsync(ct))
        {
            CronExpression.Parse(domain.CronExpression, CronFormatOf(domain.CronExpression)); // 잘못된 cron 은 여기서 실패
            _recurring.AddOrUpdate(
                recurringJobId: $"domain:{domain.DomainCode}",
                methodCall: () => FireAsync(domain.DomainCode, CancellationToken.None),
                cronExpression: domain.CronExpression,
                options: new RecurringJobOptions { TimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Seoul") });
            _log.LogInformation("[Register] {Domain} cron={Cron} queue={Queue}", domain.DomainCode, domain.CronExpression, domain.QueueName);
        }
    }

    // ── 2. Sync ───────────────────────────────────────────────────────────────
    /// <summary>주기적으로 DB ↔ Hangfire 등록 상태를 맞춘다. 콘솔에서 바꾼 설정이 배포 없이 반영되는 지점.</summary>
    public async Task SyncAsync(CancellationToken ct)
    {
        var enabled = (await _config.GetEnabledDomainsAsync(ct)).ToDictionary(d => d.DomainCode);
        foreach (var registered in RegisteredDomainIds())
        {
            if (!enabled.ContainsKey(registered))
            {
                _recurring.RemoveIfExists($"domain:{registered}");
                _log.LogInformation("[Sync] 비활성 도메인 제거 {Domain}", registered);
            }
        }
        await RegisterAllAsync(ct); // AddOrUpdate 는 멱등 — 변경분만 실제로 갱신된다
    }

    // ── 3. Fire → 4. Filter → 5. Enqueue ──────────────────────────────────────
    /// <summary>Cron 도래 시 Hangfire 가 호출. 도메인 하나를 브랜드 전체로 fan-out 한다.</summary>
    [AutomaticRetry(Attempts = 0)] // fan-out 자체는 재시도하지 않는다 — 재시도는 브랜드 실행 단위에서
    public async Task FireAsync(string domainCode, CancellationToken ct)
    {
        var firedAt = DateTimeOffset.UtcNow;
        var brands = await _resolver.ResolveAllBrandsAsync(domainCode, ct);
        var enqueued = 0;

        foreach (var schedule in brands)
        {
            if (!schedule.Enabled) continue;

            // 4. Filter — 3단 운영 제어 판정
            var decision = _gate.Evaluate(schedule.DomainCode, schedule.GroupCode, schedule.BrandCode, schedule.QueueName);
            if (decision is GateDecision.Paused or GateDecision.Blocked)
            {
                _log.LogInformation("[Filter] {Domain}/{Brand} {Decision}", schedule.DomainCode, schedule.BrandCode, decision);
                continue;
            }

            // 5. Enqueue — 도메인 큐(= 워커 서버)로. 큐 이름이 곧 장애 격리 경계.
            var request = new JobRequest(schedule, firedAt, ValidationOnly: decision == GateDecision.ValidationOnly);
            _jobs.Enqueue(schedule.QueueName, () => JobDispatcher.ExecuteAsync(request, CancellationToken.None));
            enqueued++;
        }

        _log.LogInformation("[Fire] {Domain} → {Count}/{Total} 브랜드 적재", domainCode, enqueued, brands.Count);
    }

    private static CronFormat CronFormatOf(string cron) =>
        cron.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 6 ? CronFormat.IncludeSeconds : CronFormat.Standard;

    private static IEnumerable<string> RegisteredDomainIds() =>
        JobStorage.Current.GetConnection().GetRecurringJobs()
            .Select(j => j.Id)
            .Where(id => id.StartsWith("domain:"))
            .Select(id => id["domain:".Length..]);
}

/// <summary>브랜드 단위 실행 요청 — 큐를 타고 워커로 전달되는 직렬화 가능한 단위.</summary>
public sealed record JobRequest(EffectiveSchedule Schedule, DateTimeOffset FiredAt, bool ValidationOnly);
