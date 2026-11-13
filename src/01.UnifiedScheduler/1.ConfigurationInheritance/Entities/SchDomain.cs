using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Portfolio.UnifiedScheduler.Data;

[Table("SCH_DOMAIN")]
public class SchDomain
{
    [Key]
    [Column("DOMAIN_CODE")] [MaxLength(40)]
    public string DomainCode { get; set; } = "";

    [Column("JOB_TYPE")] [MaxLength(300)]
    public string JobType { get; set; } = "";

    [Column("PARAM_DTO_TYPE")] [MaxLength(300)]
    public string? ParamDtoType { get; set; }

    [Column("DEFAULT_QUEUE")] [MaxLength(30)]
    public string DefaultQueue { get; set; } = "default";

    [Column("USE_YN")] [MaxLength(1)]
    public string UseYn { get; set; } = "Y";

    /// <summary>
    /// 테스트 도메인 마커. 'Y' 인 경우 매핑된 모든 (Domain,Brand) Job 의
    /// 실효 IsTest 가 강제로 true 가 되어 JobBase 가 ExecuteCoreAsync 만 스킵한다.
    /// </summary>
    [Column("TEST_YN")] [MaxLength(1)]
    public string TestYn { get; set; } = "N";

    /// <summary>
    /// 도메인별 에러 메일 발송 활성화 플래그. <see cref="SchDomainBrandOverride.MailSend"/> 와 AND 결합되어
    /// "둘 다 'Y' 일 때만" 메일을 발송하는 실효 게이트를 구성한다 (개발팀 시스템 알림 포함 전부 게이트).
    /// 기본 'N' — 도메인별로 명시적으로 켜야 발송 시작.
    /// </summary>
    [Column("MAIL_SEND")] [MaxLength(1)]
    public string MailSend { get; set; } = "N";

    [Column("REMARK")] [MaxLength(500)]
    public string? Remark { get; set; }

    /// <summary>
    /// 도메인 자유 설명(긴 메모). 길이 제한 없는 NVARCHAR(MAX) — 짧은 비고는 <see cref="Remark"/> 사용.
    /// 잡 실행 로직에는 관여하지 않는 표시 전용 컬럼.
    /// </summary>
    [Column("DOMAIN_MEMO")]
    public string? DomainMemo { get; set; }

    [Column("REG_DT")] public DateTime RegDt { get; set; }
    [Column("UPD_DT")] public DateTime UpdDt { get; set; }

    public SchDomainSchedule? Schedule { get; set; }
    public ICollection<SchDomainBrandOverride> Overrides { get; set; } = new List<SchDomainBrandOverride>();
    public ICollection<SchDomainGroupMap>      GroupMaps { get; set; } = new List<SchDomainGroupMap>();
}
