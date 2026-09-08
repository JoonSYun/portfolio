using System.Data;
using Cronos;
using Dapper;
using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Portfolio.UnifiedScheduler.ConfigurationInheritance;
using Portfolio.UnifiedScheduler.Jobs;
using Portfolio.UnifiedScheduler.Models;
using Portfolio.UnifiedScheduler.OperationsConsoleAndStabilization.History;

namespace Portfolio.UnifiedScheduler.OperationsConsoleAndStabilization.Api;

/// <summary>[담당업무 4] 관리 콘솔 백엔드 — 실행 이력 · 실행 예정 · 수동 실행 · DB 헬스를 한 화면에.</summary>
[ApiController, Route("api/console"), Authorize]
public sealed class ConsoleController : ControllerBase
{
    private readonly IExecutionHistoryReader _history;
    private readonly IScheduleConfigRepository _config;
    private readonly ScheduleConfigResolver _resolver;
    private readonly Dispatcher _dispatcher;
    private readonly IDbConnection _db;

    public ConsoleController(IExecutionHistoryReader history, IScheduleConfigRepository config,
        ScheduleConfigResolver resolver, Dispatcher dispatcher, IDbConnection db)
    { _history = history; _config = config; _resolver = resolver; _dispatcher = dispatcher; _db = db; }

    /// <summary>실행 이력 — 도메인/브랜드/기간 필터. 콘솔 메인 화면.</summary>
    [HttpGet("history")]
    public Task<IReadOnlyList<ExecutionHistoryRow>> History([FromQuery] HistoryQuery q, CancellationToken ct) =>
        _history.SearchAsync(q, ct);

    /// <summary>실행 예정 — 도메인별 실효 cron 으로 향후 N회 계산 (브랜드 오버라이드 반영).</summary>
    [HttpGet("upcoming")]
    public async Task<IEnumerable<object>> Upcoming([FromQuery] int hours = 24, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow; var until = now.AddHours(hours);
        var result = new List<object>();
        foreach (var domain in await _config.GetEnabledDomainsAsync(ct))
        foreach (var s in await _resolver.ResolveAllBrandsAsync(domain.DomainCode, ct))
        {
            var cron = CronExpression.Parse(s.CronExpression, s.CronExpression.Split(' ').Length >= 6 ? CronFormat.IncludeSeconds : CronFormat.Standard);
            var next = cron.GetOccurrences(now, until, TimeZoneInfo.FindSystemTimeZoneById("Asia/Seoul")).Take(10).ToArray();
            result.Add(new { s.DomainCode, s.BrandCode, s.CronExpression, s.QueueName, next });
        }
        return result;
    }

    /// <summary>
    /// 수동 실행 — 특정 브랜드 또는 도메인 전체. cron 자동 발사와 같은 <see cref="Dispatcher"/> enqueue 경로를 탄다.
    /// <c>runAs=test</c> 면 비즈니스 로직을 우회하는 테스트 실행(JobArgs.IsTest) 으로 흘린다.
    /// </summary>
    [HttpPost("run/{domainCode}")]
    [Authorize(Roles = "Operator")]
    public IActionResult Run(string domainCode, [FromQuery] string? brandCode, [FromQuery] RunAsMode runAs = RunAsMode.Operational)
    {
        var requester = User.Identity?.Name ?? "console";

        var results = brandCode is null
            ? _dispatcher.EnqueueDomain(domainCode, runAs, requester)
            : new[] { _dispatcher.EnqueueOne(domainCode, brandCode, runAs, requester) };

        return Accepted(new
        {
            domainCode,
            enqueued = results.Count(r => r.SkipReason is null),
            skipped  = results.Where(r => r.SkipReason is not null).Select(r => new { r.BrandCode, r.SkipReason, r.ActiveLogId }),
            operatorId = requester
        });
    }

    /// <summary>DB 헬스 — 연결·대기 세션·블로킹·Hangfire 큐 적체를 한 번에.</summary>
    [HttpGet("db-health")]
    public async Task<object> DbHealth(CancellationToken ct)
    {
        var blocking = await _db.QueryAsync<BlockingRow>(new CommandDefinition("""
            SELECT r.session_id AS SessionId, r.blocking_session_id AS BlockedBy, r.wait_type AS WaitType,
                   r.wait_time AS WaitMs, t.text AS SqlText
            FROM   sys.dm_exec_requests r
            CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) t
            WHERE  r.blocking_session_id <> 0;
            """, cancellationToken: ct));

        var queues = JobStorage.Current.GetMonitoringApi().Queues()
            .Select(q => new { q.Name, q.Length, Fetched = q.Fetched ?? 0 });

        return new { checkedAt = DateTime.UtcNow, blocking = blocking.ToList(), queues };
    }

    private sealed record BlockingRow(int SessionId, int BlockedBy, string WaitType, int WaitMs, string SqlText);
}
