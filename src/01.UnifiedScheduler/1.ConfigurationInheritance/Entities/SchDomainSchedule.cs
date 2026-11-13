using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Portfolio.UnifiedScheduler.Data;

[Table("SCH_DOMAIN_SCHEDULE")]
public class SchDomainSchedule
{
    [Key]
    [Column("DOMAIN_CODE")] [MaxLength(40)]
    public string DomainCode { get; set; } = "";

    [Column("CRON_EXPR")] [MaxLength(100)]
    public string CronExpr { get; set; } = "";

    [Column("TIMEZONE_ID")] [MaxLength(50)]
    public string TimezoneId { get; set; } = "Korea Standard Time";

    [Column("VALID_FROM")] public DateOnly? ValidFrom { get; set; }
    [Column("VALID_TO")]   public DateOnly? ValidTo   { get; set; }

    [Column("TIMEOUT_SEC")]     public int TimeoutSec { get; set; } = 180;

    /// <summary>
    /// 큐에 들어간 뒤 이 시간(초) 안에 실행이 시작되지 않으면 잡을 폐기(DEPRECATED)한다.
    /// <see cref="TimeoutSec"/> 은 "시작된 후" 실행 한도라 의미가 다르다 — 이쪽은 "시작 전" 대기 한도.
    /// <c>0</c> = 무제한(폐기 안 함) 이며 <b>기본값</b>이다 — 운영자가 명시적으로 값을 넣기 전까지
    /// 큐 대기 폐기를 하지 않는다(fail-open). 판정부에 <c>&gt; 0</c> 가드가 반드시 선행해야 한다.
    /// </summary>
    [Column("DEPRECATE_SEC")]   public int DeprecateSec { get; set; }

    [Column("MAX_CONCURRENCY")] public int MaxConcurrency { get; set; } = 1;

    [Column("PAYLOAD_JSON")]
    public string? PayloadJson { get; set; }

    [Column("USE_YN")] [MaxLength(1)]
    public string UseYn { get; set; } = "Y";

    [Column("UPD_DT")]        public DateTime  UpdDt { get; set; }
    [Column("SYNCED_UPD_DT")] public DateTime? SyncedUpdDt { get; set; }
    [Column("REG_DT")]        public DateTime  RegDt { get; set; }

    [ForeignKey(nameof(DomainCode))]
    public SchDomain? Domain { get; set; }
}
