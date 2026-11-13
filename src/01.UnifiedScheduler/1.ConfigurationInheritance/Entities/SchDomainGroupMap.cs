using System.ComponentModel.DataAnnotations.Schema;

namespace Portfolio.UnifiedScheduler.Data;

[Table("SCH_DOMAIN_GROUP_MAP")]
public class SchDomainGroupMap
{
    [Column("DOMAIN_CODE")]
    public string DomainCode { get; set; } = "";

    [Column("GROUP_CODE")]
    public string GroupCode { get; set; } = "";

    [Column("REG_DT")]
    public DateTime RegDt { get; set; }

    public SchDomain?      Domain { get; set; }
    public SchDomainGroup? Group  { get; set; }
}
