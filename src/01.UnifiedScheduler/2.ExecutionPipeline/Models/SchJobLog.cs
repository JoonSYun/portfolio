using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Portfolio.UnifiedScheduler.Data;

[Table("SCH_JOB_LOG")]
public class SchJobLog
{
    [Key]
    [Column("LOG_ID")] [MaxLength(40)]
    public string LogId { get; set; } = "";

    [Column("DOMAIN_CODE")] [MaxLength(40)]
    public string DomainCode { get; set; } = "";

    [Column("BRAND_CODE")] [MaxLength(10)]
    public string BrandCode { get; set; } = "";

    [Column("CENTER_CODE")] [MaxLength(20)]
    public string? CenterCode { get; set; }

    [Column("SCHEDULED_DT")] public DateTime  ScheduledDt { get; set; }
    [Column("ENQUEUED_DT")]  public DateTime  EnqueuedDt  { get; set; }
    [Column("STARTED_DT")]   public DateTime? StartedDt   { get; set; }
    [Column("FINISHED_DT")]  public DateTime? FinishedDt  { get; set; }

    [Column("STATUS")] [MaxLength(20)]
    public JobStatus Status { get; set; } = JobStatus.ENQUEUED;

    [Column("HANGFIRE_JOB_ID")] [MaxLength(50)]
    public string? HangfireJobId { get; set; }

    [Column("TIMEOUT_SEC")] public int TimeoutSec { get; set; }

    /// <summary>
    /// enqueue 시점의 큐 대기 폐기 한도 스냅샷(초). <c>0</c> = 무제한.
    /// SCH_DOMAIN_SCHEDULE 로 JOIN 하지 않고 이 값을 직접 읽어 판정한다.
    /// </summary>
    [Column("DEPRECATE_SEC")] public int DeprecateSec { get; set; }

    [Column("ERROR_MESSAGE")] [MaxLength(2000)]
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// 본 실행이 테스트 path 였는지 영속화. Dispatcher 가 (Domain/Group/Brand TEST_YN OR
    /// 수동 runAs=test) 로 계산해 JobArgs.IsTest 에 박은 값이 INSERT 시점에 그대로 저장된다.
    /// 'Y' 인 row 의 STATUS=SUCCESS + ERROR_MESSAGE="Skipped: test mode" 는 JobBase 가
    /// ExecuteCoreAsync 를 우회한 결과.
    /// </summary>
    [Column("TEST_YN")] [MaxLength(1)]
    public string TestYn { get; set; } = "N";

    /// <summary>
    /// 실행 요청자. cron 자동 발사는 'Scheduler', Admin "Run now" 는 RunNowController 가
    /// JWT NameIdentifier claim 으로 채운다. V020 이전 row 는 NULL.
    /// </summary>
    [Column("REQUESTER")] [MaxLength(100)]
    public string? Requester { get; set; }
}
