using Portfolio.UnifiedScheduler.Data;

namespace Portfolio.UnifiedScheduler.Models;

/// <summary>
/// SCH_JOB_LOG 의 활성(ENQUEUED/RUNNING) row 한 건 — Dispatcher 의 중복 enqueue 검사용
/// 최소 투영. 같은 (Domain, Brand) 에 활성 row 가 여러 개면 가장 최근 ENQUEUED 가 채택된다.
/// </summary>
public sealed record ActiveJobRow(string BrandCode, string LogId, JobStatus Status);
