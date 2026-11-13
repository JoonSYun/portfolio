using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Portfolio.UnifiedScheduler.Data;

[Table("SCH_BRAND")]
public class SchBrand
{
    [Key]
    [Column("BRAND_CODE")] [MaxLength(10)]
    public string BrandCode { get; set; } = "";

    [Column("CENTER_CODE")] [MaxLength(20)]
    public string? CenterCode { get; set; }

    [Column("DESCRIPTION")] [MaxLength(200)]
    public string? Description { get; set; }

    [Column("USE_YN")] [MaxLength(1)]
    public string UseYn { get; set; } = "Y";

    /// <summary>
    /// 테스트 브랜드 마커. 'Y' 인 경우 이 브랜드가 묶인 모든 (Domain,Brand) Job 의
    /// 실효 IsTest 가 강제 true. <see cref="SchDomain.TestYn"/> 와 OR 결합.
    /// </summary>
    [Column("TEST_YN")] [MaxLength(1)]
    public string TestYn { get; set; } = "N";

    /// <summary>
    /// 브랜드(마스터) 단위 에러 메일 발송 플래그. SchDomain.MailSend / SchDomainBrandOverride.MailSend 와
    /// AND 결합되어 셋 다 'Y' 일 때만 발송한다. 기본 'N'. 브랜드 전역 메일 킬스위치.
    /// </summary>
    [Column("MAIL_SEND")] [MaxLength(1)]
    public string MailSend { get; set; } = "N";
}
