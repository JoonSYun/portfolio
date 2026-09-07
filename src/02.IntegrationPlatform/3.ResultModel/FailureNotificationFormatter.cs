using System.Text;

namespace Portfolio.IntegrationPlatform.ResultModel;

/// <summary>
/// [담당업무 3] 실패 알림 — 건수가 아니라 "대상 주문 + 실패 지점" 을 보낸다.
///
///   [SABANGNET/BRAND_A] 97/100 성공 · 12.3s
///   실패 3건 — ExternalCommit 2, Validate 1
///   ORD-1001 ✗ ExternalCommit [E4012] 재고 없음
///   ORD-1007 ✗ ExternalCommit [E4012] 재고 없음
///   ORD-1013 ✗ Validate 허용되지 않은 창고 W09
/// </summary>
public static class FailureNotificationFormatter
{
    private const int MaxLines = 20;

    public static string? Format(IntegrationRunResult run)
    {
        if (run.FailureCount == 0) return null; // 성공은 알리지 않는다

        var sb = new StringBuilder();
        sb.AppendLine($"[{run.ConnectorCode}/{run.BrandCode}] {run.SuccessCount}/{run.TotalCount} 성공 · {run.Elapsed.TotalSeconds:0.0}s");

        var byStage = string.Join(", ", run.FailuresByStage().OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}"));
        sb.AppendLine($"실패 {run.FailureCount}건 — {byStage}");

        foreach (var f in run.Failures.Take(MaxLines))
            sb.AppendLine(f.ToString());
        if (run.FailureCount > MaxLines)
            sb.AppendLine($"… 외 {run.FailureCount - MaxLines}건 (콘솔 correlation={run.CorrelationId:N})");

        return sb.ToString();
    }
}
