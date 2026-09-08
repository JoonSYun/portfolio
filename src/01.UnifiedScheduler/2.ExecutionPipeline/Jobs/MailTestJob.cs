// [담당업무 2] 파생 Job 예시 — 운영 경로(ExecuteCoreAsync)와 테스트 경로(ExecuteTestCoreAsync), 시스템/업무 예외 라우팅을 보여준다.

using Hangfire;
using Portfolio.UnifiedScheduler.Infrastructure;
using Portfolio.UnifiedScheduler.Models;

namespace Portfolio.UnifiedScheduler.Jobs;

/// <summary>
/// 에러 메일 발송 경로를 점검하기 위한 진단 Job. 비즈니스 로직은 없고 의도적으로 예외만 던져
/// <see cref="JobBase{TSelf,TParam}.OnExceptionAsync"/> → 알림 큐 → SMTP 경로를 실제로 태운다.
///
/// <list type="bullet">
///   <item>운영 실행(<see cref="ExecuteCoreAsync"/>): 일반 <see cref="Exception"/> throw →
///         시스템 예외로 분류되어 개발팀 에게만 메일 발송.</item>
///   <item>테스트 실행(<see cref="ExecuteTestCoreAsync"/>, JobArgs.IsTest=true): <see cref="BusinessException"/> throw →
///         업무 예외로 분류되어 브랜드 추가 메일(SCH_BRAND_ADD_MAIL) + 개발팀 에게 발송.</item>
/// </list>
///
/// <para>
/// 두 경로 모두 발송 여부는 (도메인 AND 브랜드) MAIL_SEND 게이트가 최종 결정한다 — 점검 시
/// 대상 (도메인,브랜드) 의 MAIL_SEND 를 'Y' 로 켜두어야 메일이 실제로 나간다. 본 Job 은
/// SCH_JOB_LOG 상태 전이를 베이스가 관리하므로 Hangfire 재시도는 끈다.
/// </para>
/// </summary>
[AutomaticRetry(Attempts = 0)]
public class MailTestJob : JobBase<MailTestJob, MailTestParam>
{
    /// <summary>DI 생성자. 추가 의존성 없이 베이스 라이프사이클만 사용한다.</summary>
    /// <param name="logs">SCH_JOB_LOG 상태 전이 리포지토리.</param>
    /// <param name="notifier">실패 알림 채널.</param>
    /// <param name="services">추가 의존성 resolve 용 루트 컨테이너.</param>
    /// <param name="logger">파생 타입 카테고리 logger.</param>
    public MailTestJob(
        JobLogRepository    logs,
        INotifier           notifier,
        IServiceProvider    services,
        ILogger<MailTestJob> logger)
        : base(logs, notifier, services, logger)
    {
    }

    /// <summary>
    /// 운영 경로. 일반 <see cref="Exception"/> 을 던져 시스템 예외 메일(개발팀 한정) 발송을 점검한다.
    /// </summary>
    /// <param name="args">Dispatcher 가 enqueue 한 페이로드.</param>
    /// <param name="ct">shutdown + timeout 합성 토큰.</param>
    protected override Task ExecuteCoreAsync(JobArgs args, CancellationToken ct)
    {
        throw new Exception(
            $"[MailTest] 운영 메일 발송 점검용 시스템 예외 — Domain={args.DomainCode} Brand={args.BrandCode} LogId={args.LogId}");
    }

    /// <summary>
    /// 테스트 경로. <see cref="BusinessException"/> 을 던져 업무 예외 메일(브랜드 추가 메일 + 개발팀) 발송을 점검한다.
    /// </summary>
    /// <param name="args">Dispatcher 가 enqueue 한 페이로드.</param>
    /// <param name="ct">shutdown + timeout 합성 토큰.</param>
    protected override Task ExecuteTestCoreAsync(JobArgs args, CancellationToken ct)
    {
        throw new BusinessException(
            $"[MailTest] 테스트 메일 발송 점검용 업무 예외 — Domain={args.DomainCode} Brand={args.BrandCode} LogId={args.LogId}");
    }
}
