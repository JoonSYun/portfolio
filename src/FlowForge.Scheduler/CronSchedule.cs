using Cronos;

namespace FlowForge.Scheduler;

/// <summary>
/// Thin wrapper over Cronos that accepts either 5-field (standard) or 6-field
/// (seconds-included) cron expressions and returns the next UTC occurrence.
/// </summary>
public sealed class CronSchedule
{
    private readonly CronExpression _expression;

    public string Raw { get; }

    private CronSchedule(CronExpression expression, string raw)
    {
        _expression = expression;
        Raw = raw;
    }

    public static CronSchedule Parse(string cron)
    {
        var fieldCount = cron.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        var format = fieldCount >= 6 ? CronFormat.IncludeSeconds : CronFormat.Standard;
        return new CronSchedule(CronExpression.Parse(cron, format), cron);
    }

    public DateTimeOffset? Next(DateTimeOffset after)
    {
        var next = _expression.GetNextOccurrence(after.UtcDateTime, inclusive: false);
        return next.HasValue ? new DateTimeOffset(next.Value, TimeSpan.Zero) : null;
    }
}
