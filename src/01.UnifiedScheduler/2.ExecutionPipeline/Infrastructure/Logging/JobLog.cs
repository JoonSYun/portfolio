using Portfolio.UnifiedScheduler.Models;

namespace Portfolio.UnifiedScheduler.Infrastructure;

// ═══════════════════════════════════════════════════════════════════════════
//  JobLog — Job / Dispatch scoped log extension methods
// ═══════════════════════════════════════════════════════════════════════════
//
//  Every Job / Dispatcher log line should carry Domain / Brand / Center /
//  LogId / Bucket in the message body so a single plain-text line is enough
//  to understand the context.
//
//  Rather than hand-concatenating those fields at every call site, this class
//  provides ILogger extension methods that expand a compact call into the
//  full template. All templates are wrapped through <see cref="AppLog.Log"/>
//  so the application-name prefix is applied uniformly.
//
//  Usage:
//      _logger.LogJobContext(args, "Return started");
//      _logger.ErrorLogJobContext(args, ex, "Return failed");
//      _logger.LogDispatchContext(domainCode, bucketedUtc, "Dispatch started");
//
//  Why extension methods:
//    - `this ILogger` preserves the caller's SourceContext (log category).
//    - A static helper with its own Log.XXX would pollute the category to
//      "JobLog" (anti-pattern).
//
//  Structured logging:
//    {DomainCode}, {BrandCode}, etc. are emitted as structured Serilog
//    properties, so Seq filters like DomainCode='RETURN_LOGEN' still work.
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// ILogger extension methods that include Job / Dispatch context and apply
/// the unified <see cref="AppLog"/> prefix.
/// </summary>
public static class JobLog
{
    private const string JobBody =
        "{AddMessage} │ Domain={DomainCode} Brand={BrandCode} Center={CenterCode} LogId={LogId} Bucket={BucketedDt:yyyy-MM-dd HH:mm}";

    private const string DispatchBody =
        "{AddMessage} │ Domain={DomainCode} Bucket={BucketedDt:yyyy-MM-dd HH:mm}";

    // ─── Job context (after brand fan-out) ─────────────────────────────────

    public static void LogJobContext(
        this ILogger logger,
        JobArgs      args,
        string       addMessage)
    {
        logger.LogInformation(AppLog.Log(JobBody),
            addMessage, args.DomainCode, args.BrandCode, args.CenterCode, args.LogId, args.BucketedDt);
    }

    public static void WarnLogJobContext(
        this ILogger logger,
        JobArgs      args,
        string       addMessage)
    {
        logger.LogWarning(AppLog.Log(JobBody),
            addMessage, args.DomainCode, args.BrandCode, args.CenterCode, args.LogId, args.BucketedDt);
    }

    public static void ErrorLogJobContext(
        this ILogger logger,
        JobArgs      args,
        Exception    ex,
        string       addMessage)
    {
        logger.LogError(ex, AppLog.Log(JobBody),
            addMessage, args.DomainCode, args.BrandCode, args.CenterCode, args.LogId, args.BucketedDt);
    }

    public static void DebugLogJobContext(
        this ILogger logger,
        JobArgs      args,
        string       addMessage)
    {
        logger.LogDebug(AppLog.Log(JobBody),
            addMessage, args.DomainCode, args.BrandCode, args.CenterCode, args.LogId, args.BucketedDt);
    }

    // ─── Dispatch context (before brand fan-out, no JobArgs yet) ───────────

    public static void LogDispatchContext(
        this ILogger logger,
        string       domainCode,
        DateTime     bucketedUtc,
        string       addMessage)
    {
        logger.LogInformation(AppLog.Log(DispatchBody), addMessage, domainCode, bucketedUtc);
    }

    public static void ErrorLogDispatchContext(
        this ILogger logger,
        string       domainCode,
        DateTime     bucketedUtc,
        Exception    ex,
        string       addMessage)
    {
        logger.LogError(ex, AppLog.Log(DispatchBody), addMessage, domainCode, bucketedUtc);
    }
}
