using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Portfolio.UnifiedScheduler.Data;

[Table("SCH_DOMAIN_GROUP")]
public class SchDomainGroup
{
    [Key]
    [Column("GROUP_CODE")] [MaxLength(20)]
    public string GroupCode { get; set; } = "";

    [Column("DESCRIPTION")] [MaxLength(200)]
    public string? Description { get; set; }

    [Column("SORT_ORDER")]
    public int SortOrder { get; set; }

    [Column("USE_YN")] [MaxLength(1)]
    public string UseYn { get; set; } = "Y";

    /// <summary>
    /// 테스트 그룹 마커. 'Y' 인 경우 이 그룹에 매핑된 모든 도메인의 Job 실효 IsTest
    /// 가 강제 true (SCH_DOMAIN_GROUP_MAP 기준 N:M, 매핑된 그룹 중 하나라도 'Y' 면 충분).
    /// </summary>
    [Column("TEST_YN")] [MaxLength(1)]
    public string TestYn { get; set; } = "N";

    [Column("REG_DT")] public DateTime RegDt { get; set; }
    [Column("UPD_DT")] public DateTime UpdDt { get; set; }
}
