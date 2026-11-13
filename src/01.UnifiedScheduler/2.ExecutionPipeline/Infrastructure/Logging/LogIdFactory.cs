namespace Portfolio.UnifiedScheduler.Infrastructure;

// ═══════════════════════════════════════════════════════════════════════════
//  LogIdFactory — Centralized LOG_ID generator
// ═══════════════════════════════════════════════════════════════════════════
//
//  Purpose:
//    Single source for SCH_JOB_LOG.LOG_ID and Admin API per-request log
//    correlation IDs. Callers go through this factory instead of calling
//    Guid.NewGuid() directly so the format can change later (e.g. ULID,
//    Snowflake, prefixed type tag) without touching every call site.
//
//  Current format:
//    32-char lowercase hex — Guid "N" format. Fits in NVARCHAR(40).
//
//  Usage:
//      var logId = LogIdFactory.New();
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Generates LOG_ID values for SCH_JOB_LOG rows and per-request API log
/// correlation. Centralized so the underlying format (currently Guid "N")
/// can be swapped without touching call sites.
/// </summary>
public static class LogIdFactory
{
    /// <summary>
    /// Returns a fresh LOG_ID. Current implementation is a 32-char lowercase
    /// hex string from <see cref="Guid.NewGuid"/>.
    /// </summary>
    public static string New() => Guid.NewGuid().ToString("N");
}
