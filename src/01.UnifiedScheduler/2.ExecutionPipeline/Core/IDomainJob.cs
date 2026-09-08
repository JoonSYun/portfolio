// [담당업무 2] 파생 Job 69종이 공유하는 단일 진입점 계약.

using Portfolio.UnifiedScheduler.Models;

namespace Portfolio.UnifiedScheduler.Jobs;

/// <summary>
/// 모든 도메인 Job(ReturnJob, TraceJob, ...)의 단일 진입점.
/// Dispatcher가 Type.GetType(JOB_TYPE_FQN) 으로 해석하고
/// Hangfire Worker가 이 ExecuteAsync 를 MethodInfo.Invoke 로 호출한다.
/// Hangfire 1.8 은 Task 반환 메서드를 자동 await.
///
/// <para>
/// <paramref name="ct"/> 는 Hangfire 1.8 이 자동 주입하는 worker shutdown / job 삭제 신호.
/// Dispatcher 가 enqueue 할 때 placeholder(<c>CancellationToken.None</c>) 를 넣고 worker 시점에
/// Hangfire 가 실제 토큰으로 교체한다. <see cref="JobBase{TSelf,TParam}"/> 는 이 토큰과
/// <see cref="JobArgs.TimeoutSec"/> 기반 timeout 을 LinkedTokenSource 로 합쳐 leaf 까지 흘려보낸다.
/// </para>
/// </summary>
public interface IDomainJob
{
    Task ExecuteAsync(JobArgs args, CancellationToken ct);
}
