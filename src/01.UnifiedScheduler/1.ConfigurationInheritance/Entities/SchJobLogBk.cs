using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Portfolio.UnifiedScheduler.Data;

/// <summary>
/// SCH_JOB_LOG 의 archive snapshot. JobLogArchiveJob 이 retention 경과 + 터미널
/// status row 만 골라 옮긴다. 컬럼은 SCH_JOB_LOG 와 1:1 매칭 + ARCHIVED_UTC 추가.
/// FK / CHECK 제약은 의도적으로 비포함 (V012 migration 주석 참조).
/// </summary>
[Table("SCH_JOB_LOG_BK")]
public class SchJobLogBk
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

    [Column("SCHEDULED_DT")] public DateTime ScheduledDt { get; set; }
    [Column("ENQUEUED_DT")] public DateTime EnqueuedDt { get; set; }
    [Column("STARTED_DT")] public DateTime? StartedDt { get; set; }
    [Column("FINISHED_DT")] public DateTime? FinishedDt { get; set; }

    [Column("STATUS")] [MaxLength(20)]
    public JobStatus Status { get; set; } = JobStatus.SUCCESS;

    [Column("HANGFIRE_JOB_ID")] [MaxLength(50)]
    public string? HangfireJobId { get; set; }

    [Column("TIMEOUT_SEC")] public int TimeoutSec { get; set; }

    /// <summary>핫 테이블(SCH_JOB_LOG.DEPRECATE_SEC)에서 그대로 복제된다.</summary>
    [Column("DEPRECATE_SEC")] public int DeprecateSec { get; set; }

    [Column("ERROR_MESSAGE")] [MaxLength(2000)]
    public string? ErrorMessage { get; set; }

    /// <summary>핫 테이블(SCH_JOB_LOG.TEST_YN)에서 그대로 복제된다.</summary>
    [Column("TEST_YN")] [MaxLength(1)]
    public string TestYn { get; set; } = "N";

    /// <summary>핫 테이블(SCH_JOB_LOG.REQUESTER)에서 그대로 복제된다. V020 이전 row 는 NULL.</summary>
    [Column("REQUESTER")] [MaxLength(100)]
    public string? Requester { get; set; }

    [Column("ARCHIVED_DT")] public DateTime ArchivedDt { get; set; }
}
