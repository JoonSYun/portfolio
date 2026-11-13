using Hangfire.Common;
using Hangfire.States;
using Hangfire.Storage;

namespace Portfolio.UnifiedScheduler.Infrastructure;

/// <summary>
/// Hangfire <see cref="IApplyStateFilter"/> — 잡이 상태 전이 직후 <see cref="ApplyStateContext.JobExpirationTimeout"/>
/// 을 외부 설정(<see cref="HangfireOptions.RetentionSection"/>)으로 덮어쓴다. <c>[HangFire].[Job].ExpireAt</c>
/// 컬럼에 박히는 TTL 을 일괄 제어해 운영 보존 정책을 코드/appsettings 한 곳에서 통제할 수 있게 한다.
/// </summary>
/// <remarks>
/// Hangfire 1.8 부터 <c>SucceededState.DefaultExpiration</c> 같은 정적 default 가 제거됐기 때문에
/// (각 상태가 ctor 단계에서 자기 <c>ExpireIn</c> 을 결정해 들고 들어옴), 전역 TTL 정책은 필터로
/// <see cref="ApplyStateContext.JobExpirationTimeout"/> 을 갈아끼우는 방식이 표준 패턴이다.
/// <para/>
/// 적용 대상은 <see cref="SucceededState"/> / <see cref="DeletedState"/> 두 종류 — 이 둘만이 ExpireAt 이
/// 박혀 자동 청소 대상이 된다. <see cref="FailedState"/> 는 의도적으로 영구 보존이라 건드리지 않는다
/// (운영자가 대시보드에서 retry/delete 하기 전까지 살아있어야 하기 때문). 이 솔루션은 자체 SCH_JOB_LOG
/// 가 retry/실패 이력을 따로 들고 있어 Hangfire 의 Failed 행이 누적되는 게 부담이라면, 워커 필터
/// (<c>AutomaticRetryAttribute.OnAttemptsExceeded = Delete</c>) 로 Failed → Deleted 강등시켜 이 필터가
/// 처리하도록 흘리는 게 정공법.
/// </remarks>
public sealed class StateExpirationFilter : IApplyStateFilter
{
    private readonly TimeSpan _succeededExpiration;
    private readonly TimeSpan _deletedExpiration;

    public StateExpirationFilter(TimeSpan succeededExpiration, TimeSpan deletedExpiration)
    {
        _succeededExpiration = succeededExpiration;
        _deletedExpiration   = deletedExpiration;
    }

    public void OnStateApplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
        switch (context.NewState)
        {
            case SucceededState:
                context.JobExpirationTimeout = _succeededExpiration;
                break;
            case DeletedState:
                context.JobExpirationTimeout = _deletedExpiration;
                break;
        }
    }

    public void OnStateUnapplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
    }
}
