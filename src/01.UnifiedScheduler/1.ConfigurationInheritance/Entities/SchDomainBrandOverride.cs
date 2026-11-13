using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Portfolio.UnifiedScheduler.Data;

[Table("SCH_DOMAIN_BRAND_OVERRIDE")]
public class SchDomainBrandOverride
{
    [Column("DOMAIN_CODE")] [MaxLength(40)]
    public string DomainCode { get; set; } = "";

    [Column("BRAND_CODE")] [MaxLength(10)]
    public string BrandCode { get; set; } = "";

    [Column("CENTER_CODE")] [MaxLength(20)]
    public string? CenterCode { get; set; }

    [Column("CRON_EXPR")] [MaxLength(100)]
    public string? CronExpr { get; set; }

    [Column("TIMEZONE_ID")] [MaxLength(50)]
    public string? TimezoneId { get; set; }

    [Column("VALID_FROM")] public DateOnly? ValidFrom { get; set; }
    [Column("VALID_TO")]   public DateOnly? ValidTo   { get; set; }

    [Column("TIMEOUT_SEC")] public int? TimeoutSec { get; set; }

    /// <summary>
    /// 큐 대기 폐기 한도 브랜드별 덮어쓰기. <c>null</c> = 도메인 스케줄 값 상속, <c>0</c> = 무제한.
    /// </summary>
    [Column("DEPRECATE_SEC")] public int? DeprecateSec { get; set; }

    [Column("PAYLOAD_JSON")]
    public string? PayloadJson { get; set; }

    [Column("QUEUE")] [MaxLength(30)]
    public string? Queue { get; set; }

    [Column("USE_YN")] [MaxLength(1)]
    public string UseYn { get; set; } = "Y";

    /// <summary>
    /// 도메인×브랜드 에러 메일 발송 플래그. <see cref="SchDomain.MailSend"/> 와 AND 결합 —
    /// 둘 중 하나라도 'N' 이면 이 (도메인,브랜드) 의 모든 에러 메일이 차단된다. 기본 'N'.
    /// </summary>
    [Column("MAIL_SEND")] [MaxLength(1)]
    public string MailSend { get; set; } = "N";

    [Column("REG_DT")] public DateTime RegDt { get; set; }
    [Column("UPD_DT")] public DateTime UpdDt { get; set; }

    [ForeignKey(nameof(DomainCode))] public SchDomain? Domain { get; set; }
    [ForeignKey(nameof(BrandCode))]  public SchBrand?  Brand  { get; set; }

    /// <summary>시간대별 cron 자식 (SCH_DOMAIN_BRAND_CRON). 0개면 도메인 L1 상속.</summary>
    public ICollection<SchDomainBrandCron> Crons { get; set; } = new List<SchDomainBrandCron>();
}
