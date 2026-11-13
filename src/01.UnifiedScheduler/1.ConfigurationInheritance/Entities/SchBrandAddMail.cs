using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Portfolio.UnifiedScheduler.Data;

/// <summary>
/// SCH_BRAND_ADD_MAIL — 브랜드별 추가 에러 메일 수신자 (1:N).
/// BusinessException 발생 시 NotificationJob 이 BRAND_CODE 로 USE_YN='Y' 인 이메일을 조회해
/// 개발팀 수신자에 합쳐 발송한다. 일반(시스템) Exception 은 본 테이블을 조회하지 않는다.
/// </summary>
[Table("SCH_BRAND_ADD_MAIL")]
public class SchBrandAddMail
{
    [Key]
    [Column("ID")]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [Column("BRAND_CODE")] [MaxLength(10)]
    public string BrandCode { get; set; } = "";

    [Column("EMAIL")] [MaxLength(200)]
    public string Email { get; set; } = "";

    [Column("USE_YN")] [MaxLength(1)]
    public string UseYn { get; set; } = "Y";

    [Column("REG_DT")] public DateTime RegDt { get; set; }
    [Column("UPD_DT")] public DateTime UpdDt { get; set; }

    [Column("REG_USER")] [MaxLength(100)]
    public string? RegUser { get; set; }

    [Column("UPD_USER")] [MaxLength(100)]
    public string? UpdUser { get; set; }

    [ForeignKey(nameof(BrandCode))] public SchBrand? Brand { get; set; }
}
