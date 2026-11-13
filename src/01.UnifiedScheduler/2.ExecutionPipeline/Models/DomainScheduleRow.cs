namespace Portfolio.UnifiedScheduler.Models;

/// <summary>
/// Bootstrapper와 Sync Job이 읽는 SCH_DOMAIN × SCH_DOMAIN_SCHEDULE 조인 결과.
/// </summary>
public class DomainScheduleRow
{
    public string   DomainCode   { get; set; } = "";
    public string   DefaultQueue { get; set; } = "default";
    public string   JobType      { get; set; } = "";
    public string   CronExpr     { get; set; } = "";
    public string   TimezoneId   { get; set; } = "Korea Standard Time";
    public DateTime UpdDt        { get; set; }
}
