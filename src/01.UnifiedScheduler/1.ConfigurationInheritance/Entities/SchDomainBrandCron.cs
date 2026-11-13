using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Portfolio.UnifiedScheduler.Data;

/// <summary>
/// SCH_DOMAIN_BRAND_CRON — 브랜드 override(L2) 의 시간대별 cron 자식.
/// (DOMAIN_CODE, BRAND_CODE) 당 N개. 디스패처는 활성 cron 중 이번 tick 에 매치되는 게
/// 하나라도 있으면(OR) 1회 발사 — 겹쳐도 무해. cron 0개면 도메인 L1 스케줄을 상속.
/// 부모 SCH_DOMAIN_BRAND_OVERRIDE 와 복합 FK(ON DELETE CASCADE).
/// </summary>
[Table("SCH_DOMAIN_BRAND_CRON")]
public class SchDomainBrandCron
{
    [Key] [Column("ID")]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [Column("DOMAIN_CODE")] [MaxLength(40)]
    public string DomainCode { get; set; } = "";

    [Column("BRAND_CODE")] [MaxLength(10)]
    public string BrandCode { get; set; } = "";

    [Column("CRON_EXPR")] [MaxLength(100)]
    public string CronExpr { get; set; } = "";

    [Column("TIMEZONE_ID")] [MaxLength(50)]
    public string? TimezoneId { get; set; }

    [Column("VALID_FROM")] public DateOnly? ValidFrom { get; set; }
    [Column("VALID_TO")]   public DateOnly? ValidTo   { get; set; }

    [Column("USE_YN")] [MaxLength(1)]
    public string UseYn { get; set; } = "Y";

    [Column("REG_DT")] public DateTime RegDt { get; set; }
    [Column("UPD_DT")] public DateTime UpdDt { get; set; }

    [Column("REG_USER")] [MaxLength(100)]
    public string? RegUser { get; set; }

    [Column("UPD_USER")] [MaxLength(100)]
    public string? UpdUser { get; set; }

    public SchDomainBrandOverride? Override { get; set; }
}
