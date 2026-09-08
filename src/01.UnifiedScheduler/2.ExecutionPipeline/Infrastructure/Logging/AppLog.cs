namespace Portfolio.UnifiedScheduler.Infrastructure;

// ═══════════════════════════════════════════════════════════════════════════
//  AppLog — Unified log prefix wrapper
// ═══════════════════════════════════════════════════════════════════════════
//
//  Purpose:
//    Any log message passed through AppLog.Log() is prefixed with the
//    application name so the source service is immediately identifiable in
//    mixed log streams (Console / File / Seq).
//
//  Usage (same pattern as the orchestration platform AppLog):
//
//      _logger.LogInformation(
//          AppLog.Log("[Domain.Job] Submit brand={Brand}"), brandCode);
//
//      Log.Information(AppLog.Log("Starting..."));
//
//  Output example:
//      [Portfolio.UnifiedScheduler] - [Domain.Job] Submit brand=13
//
//  Configuration:
//    ApplicationName is injected from appsettings.json in Program.cs via
//    AppLog.ApplicationName = "..." immediately after configuration is read.
//    Default fallback keeps prefix meaningful even if init order slips.
//
//  Note:
//    The timestamp is already emitted by the Serilog outputTemplate so it is
//    NOT duplicated here. For Job / Dispatch scoped logs that also need to
//    carry Domain / Brand / Center / LogId / Bucket, use JobLog extension
//    methods which wrap their templates through AppLog.Log() as well.
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Static helper that prefixes every log message with the application name
/// so the log source is uniformly identifiable across all services.
/// </summary>
public static class AppLog
{
    /// <summary>
    /// Application name used as the log prefix. Set once at startup from
    /// <c>appsettings.json</c>'s <c>ApplicationName</c> key in <c>Program.cs</c>.
    /// </summary>
    public static string ApplicationName { get; set; } = "Portfolio.UnifiedScheduler";

    /// <summary>
    /// Wraps <paramref name="message"/> with a fixed <c>[ApplicationName] - </c> prefix.
    /// The returned string preserves any Serilog placeholders in <paramref name="message"/>
    /// (e.g. <c>{Brand}</c>), so structured logging remains intact.
    /// </summary>
    public static string Log(string message) => $"[{ApplicationName}] - {message}";
}
