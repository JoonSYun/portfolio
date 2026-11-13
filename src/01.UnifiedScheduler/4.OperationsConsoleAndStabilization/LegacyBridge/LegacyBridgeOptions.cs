namespace Portfolio.UnifiedScheduler.Infrastructure;

/// <summary>
/// <c>Portfolio.UnifiedScheduler.LegacyBridge.exe</c> 경로 옵션.
/// appsettings.json 의 "LegacyBridge" 섹션과 매핑.
///
/// <para>
/// 빈 값이면 <see cref="AppContext.BaseDirectory"/> 기준 <c>LegacyBridge/Portfolio.UnifiedScheduler.LegacyBridge.exe</c>
/// 를 기본 탐색 (배포 관행상 Scheduler exe 바로 옆에 LegacyBridge 폴더 배포).
/// </para>
/// </summary>
public class LegacyBridgeOptions
{
    /// <summary>helper exe 절대 경로. 비워두면 <see cref="DefaultPath"/> 사용.</summary>
    public string ExePath { get; set; } = "";

    /// <summary>Process 호출 타임아웃. 기본 60초.</summary>
    public int TimeoutSeconds { get; set; } = 60;

    public static string DefaultPath =>
        Path.Combine(AppContext.BaseDirectory, "LegacyBridge", "Portfolio.UnifiedScheduler.LegacyBridge.exe");
}
