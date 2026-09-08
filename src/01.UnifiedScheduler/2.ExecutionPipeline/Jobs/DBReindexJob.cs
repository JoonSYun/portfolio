// [담당업무 2] 파생 Job 예시 — 베이스가 라이프사이클을 전부 맡으므로 ExecuteCoreAsync 하나만 구현한다.

using Portfolio.UnifiedScheduler.Infrastructure;
using Portfolio.UnifiedScheduler.Infrastructure.Database;
using Portfolio.UnifiedScheduler.Models;

namespace Portfolio.UnifiedScheduler.Jobs;

/// <summary>
/// Reindexing Job — 레거시 SQL Agent "*_Reindexing" 스텝(테이블별 <c>dbcc dbreindex(테이블, '', 95)</c>)을 흡수.
/// 대상 테이블/fillfactor 를 브랜드 파라미터로 쪼개면 브랜드당 한 테이블만 설정 가능하므로,
/// 목록은 프로시저 <c>USP_SCH_인덱스재구성_Set</c>(WMS 운영 DB) 안에 두고 잡은 호출만 한다.
/// </summary>
public class DBReindexJob : JobBase<DBReindexJob, DBReindexParam>
{
    private readonly string _procedureName = "USP_SCH_인덱스재구성_Set";
    private readonly SchedulerConnectionFactory _connFactory;

    public DBReindexJob(
        JobLogRepository logs,
        INotifier notifier,
        IServiceProvider services,
        ILogger<DBReindexJob> logger,
        SchedulerConnectionFactory connFactory)
        : base(logs, notifier, services, logger)
    {
        _connFactory = connFactory;
    }

    protected override async Task ExecuteCoreAsync(JobArgs args, CancellationToken ct)
    {
        // 서버 전역 유지보수라 SP 는 파라미터를 받지 않는다(USP_SCH_인덱스재구성_Set 무인자).
        // 브랜드별 호출 형태가 아니므로 Param.BCODE 는 SP 로 넘기지 않는다 — payload 의 BCODE 는
        // ISchedulerParam 계약/디스패치/검증용일 뿐 SP 본문과 무관.
        using ISchedulerConnection conn = await _connFactory.CreateAsync(DBNames.WmsMain);

        // commandTimeout 을 DB 설정(JobArgs.TimeoutSec)과 동일하게 두고 ct 까지 전달 — 타임아웃/외부 취소 시 서버 단 실행 중단.
        await conn.ExecuteAsync(_procedureName, commandTimeout: args.TimeoutSec, cancellationToken: ct);
    }
}
