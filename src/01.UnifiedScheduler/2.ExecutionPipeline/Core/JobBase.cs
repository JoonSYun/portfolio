// [담당업무 2] 공통 Job 베이스 — 역직렬화·검증·상태 전이·타임아웃 전파·실패 마감·알림까지의 실행 라이프사이클을 한 곳에서 통일한다.

using System.ComponentModel.DataAnnotations;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Serilog.Context;
using Portfolio.UnifiedScheduler.Infrastructure;
using Portfolio.UnifiedScheduler.Models;

namespace Portfolio.UnifiedScheduler.Jobs;

/// <summary>
/// 모든 도메인 Job(TraceJob, ReturnJob, RecallJob, ...)이 상속하는 베이스.
/// Hangfire worker 가 <see cref="ExecuteAsync"/> 를 호출하는 단일 진입점을 제공하고,
/// 페이로드 역직렬화 → 검증 → ENQUEUED→RUNNING 전이 → <see cref="ExecuteCoreAsync"/> 호출 →
/// 결과에 따른 SUCCESS/FAILED 전이 → 실패 알림까지의 공통 라이프사이클을 한 곳에서 처리한다.
/// 파생 클래스는 비즈니스 로직(<see cref="ExecuteCoreAsync"/>)만 구현하면 된다.
///
/// <para>
/// 라이프사이클은 단일 try/catch 로 감싸여 있다. 비즈니스 로직 *이후* 단계(<see cref="SafeMarkSuccess"/>·
/// <see cref="SafeMarkSuccessAsTestSkipped"/>·<see cref="SafeMarkFailed"/>·<see cref="SafeNotify"/>) 만 자체 예외를
/// swallow 하고, 그 외의 모든 실패 — 페이로드 역직렬화·검증·<see cref="JobLogRepository.MarkRunningOrThrow"/>
/// 의 비정상 상태·비즈니스 예외 — 는 동일한 catch 로 모여 FAILED 마감 + 알림 + Hangfire rethrow 로 일관 처리된다.
/// 정상 race(이미 DEPRECATED) 만 <see cref="MarkRunningOutcome.AlreadyDeprecated"/> 분기로 빠져 조용히 종료한다.
/// </para>
///
/// <para>
/// <typeparamref name="TSelf"/> 는 CRTP 로 <see cref="ILogger{TSelf}"/> 카테고리를 파생 타입 이름에
/// 묶기 위한 것이고, <typeparamref name="TParam"/> 은 <see cref="JobArgs.PayloadJson"/> 이
/// 역직렬화될 page-level 페이로드 DTO(예: TraceFireParam).
/// </para>
/// </summary>
/// <typeparam name="TSelf">CRTP self-type. <see cref="ILogger{TSelf}"/> 카테고리 결정에 사용.</typeparam>
/// <typeparam name="TParam">PayloadJson 이 역직렬화될 페이로드 DTO 타입.</typeparam>
public abstract class JobBase<TSelf, TParam> : IDomainJob
    where TSelf  : JobBase<TSelf, TParam>
    where TParam : class, new()
{
    /// <summary>
    /// <see cref="JobArgs.PayloadJson"/> 역직렬화 옵션. Dispatcher 측 직렬화와 Job 측 역직렬화 사이의
    /// 프로퍼티 명 케이스 차이(PascalCase ↔ camelCase 등)를 흡수하기 위해 case-insensitive.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>SCH_JOB_LOG 상태 전이를 담당하는 리포지토리. <see cref="JobLogRepository"/>.</summary>
    protected readonly JobLogRepository Logs;

    /// <summary>Job 실패 시 운영자에게 보내는 알림 채널. <see cref="INotifier"/>.</summary>
    protected readonly INotifier        Notifier;

    /// <summary>파생 Job 이 추가 의존성을 lazy resolve 할 때 쓰는 루트 컨테이너.</summary>
    protected readonly IServiceProvider Services;

    /// <summary>파생 타입 이름을 카테고리로 가지는 logger. AppLog/JobLog 확장 메서드의 진입점.</summary>
    protected readonly ILogger<TSelf>   Logger;

    /// <summary>
    /// <see cref="ExecuteAsync"/> 초입에서 <see cref="JobArgs.PayloadJson"/> 으로부터 역직렬화된
    /// 페이로드 인스턴스. <see cref="ExecuteCoreAsync"/> 안에서 읽기 전용으로 사용한다.
    /// </summary>
    protected TParam Param { get; private set; } = new TParam();

    /// <summary>
    /// DI 컨테이너에서 호출되는 생성자. 인스턴스는 Job 단위(Hangfire 호출당)로 한 번 만들어진다.
    /// </summary>
    /// <param name="logs">SCH_JOB_LOG 상태 전이 리포지토리.</param>
    /// <param name="notifier">실패 알림 채널.</param>
    /// <param name="services">파생 Job 의 추가 의존성 resolve 용 루트 컨테이너.</param>
    /// <param name="logger">파생 타입 카테고리 logger.</param>
    protected JobBase(
        JobLogRepository logs,
        INotifier        notifier,
        IServiceProvider services,
        ILogger<TSelf>   logger)
    {
        Logs     = logs;
        Notifier = notifier;
        Services = services;
        Logger   = logger;
    }

    /// <summary>
    /// 로그 메시지에 박히는 Job 이름. 기본은 파생 타입의 단순 클래스명이며 필요 시 override 가능.
    /// </summary>
    protected virtual string JobName => GetType().Name;

    /// <summary>
    /// 파생 Job 이 구현하는 실제 비즈니스 로직. <see cref="Param"/> 은 이미 역직렬화·검증이 끝난
    /// 상태로 제공되며, <paramref name="ct"/> 는 worker shutdown 과 <see cref="JobArgs.TimeoutSec"/>
    /// 기반 timeout 이 합쳐진 linked token 이라 leaf 까지 그대로 흘려보내면 된다.
    /// </summary>
    /// <param name="args">Dispatcher 가 enqueue 한 페이로드. <see cref="JobArgs"/>.</param>
    /// <param name="ct">shutdown + timeout 이 합쳐진 linked cancellation token.</param>
    protected abstract Task ExecuteCoreAsync(JobArgs args, CancellationToken ct);

    /// <summary>
    /// 테스트 모드(<see cref="JobArgs.IsTest"/>=true) 에서 <see cref="ExecuteCoreAsync"/> 대신 실행되는 훅.
    /// 기본 구현은 no-op — 파생 Job 이 필요 시에만 override 한다(필수 아님). 운영 부작용 없이
    /// 페이로드/외부 연결/직렬화 등을 가볍게 점검(dry-run)하는 용도.
    ///
    /// <para>
    /// 본 훅은 <see cref="ExecuteAsync"/> 의 기존 try/catch 안에서 호출되므로, 여기서 예외가 나면
    /// 운영 경로와 동일하게 FAILED 마감 + 알림(<see cref="OnExceptionAsync"/>) 까지 그대로 탄다.
    /// 정상 종료 시에는 RUNNING → SUCCESS("Skipped: test mode") 로 마감된다.
    /// </para>
    /// </summary>
    /// <param name="args">Dispatcher 가 enqueue 한 페이로드.</param>
    /// <param name="ct">shutdown + timeout 이 합쳐진 linked cancellation token.</param>
    protected virtual Task ExecuteTestCoreAsync(JobArgs args, CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Hangfire worker 가 호출하는 Job 진입점. 페이로드 역직렬화 → 검증 → ENQUEUED→RUNNING 전이 →
    /// <see cref="ExecuteCoreAsync"/> 실행 → SUCCESS/FAILED 전이 → 실패 시 알림까지의 라이프사이클을
    /// 단일 try/catch 로 묶어 직렬 수행한다.
    ///
    /// <para>
    /// 실패 처리 원칙: "비즈니스 결과를 SCH_JOB_LOG 에 못 적은 사고" 만 swallow 하고 그 외 어디서 깨지든
    /// 무조건 FAILED 로 마감 + 알림 + Hangfire rethrow. 페이로드 역직렬화·검증 실패, <see cref="JobLogRepository.MarkRunningOrThrow"/>
    /// 가 비정상 상태(row 없음 / SUCCESS / FAILED / 다른 워커의 RUNNING)로 throw 한 케이스, 비즈니스 예외 —
    /// 모두 catch 블록 한 곳으로 수렴한다. <see cref="SafeMarkSuccess"/>·<see cref="SafeMarkFailed"/>·<see cref="SafeNotify"/>
    /// 만 자체 예외를 swallow 하며, 그 안에서 발생한 부수 예외는 원본 'failure' 를 절대 덮지 않는다.
    /// </para>
    ///
    /// <para>
    /// 정상 race 만 분기: <see cref="JobLogRepository.MarkRunningOrThrow"/> 가 <see cref="MarkRunningOutcome.AlreadyDeprecated"/>
    /// 를 돌려주면 sweeper 가 이미 정상 마감한 row 이므로 비즈니스 건너뛰고 조용히 종료(알림 발송 없음).
    /// </para>
    ///
    /// <para>
    /// rethrow 는 <see cref="ExceptionDispatchInfo.Throw()"/> 로 원본 stack trace 를 보존해 Hangfire 의 retry/dead-letter
    /// 파이프라인이 의미 있는 진단을 받게 한다.
    /// </para>
    /// </summary>
    /// <param name="args">Dispatcher 가 enqueue 한 페이로드. <see cref="JobArgs"/>.</param>
    /// <param name="ct">Hangfire 가 주입하는 worker shutdown / job 삭제 토큰.</param>
    public async Task ExecuteAsync(JobArgs args, CancellationToken ct)
    {
        try
        {
            // [Serilog enrich] 본 Job 이 만들어내는 모든 로그 라인에 LogId 프로퍼티를 자동 부착해
            // SCH_JOB_LOG row 한 줄과 Serilog sink(파일/콘솔) 의 라인을 후행 추적 가능하게 묶는다.
            // using var _ 의 '_' 는 LogContext 가 반환하는 IDisposable 을 ExecuteAsync 종료 시점까지 살려두기 위한 placeholder 이며,
            // 메서드가 끝나면 자동 Dispose 되어 enrich 가 다른 Job 컨텍스트로 새지 않는다.
            using var _ = LogContext.PushProperty("LogId", args.LogId);

            // [진입 로그] DomainCode/BrandCode/LogId 가 enrich 된 상태로 "Job 이 worker 에 의해 픽업됨" 사실을 한 줄 남긴다.
            // 운영에서 "ENQUEUED 만 보이고 시작이 안 됐다" vs "시작은 했는데 멈췄다" 를 구분하는 첫 단서가 된다.
            Logger.LogJobContext(args, $"{JobName} started");

            // [페이로드 역직렬화] JobArgs.PayloadJson(string) 을 도메인별 DTO(TParam) 로 변환.
            // 빈 PayloadJson 검증은 <see cref="DeserializePayload"/> 내부에서 fail-fast — throw 되면 본 메서드의 catch 로 떨어져 FAILED 마감.
            Param = DeserializePayload(args);

            // [페이로드 검증] <typeparamref name="TParam"/> 에 붙은 DataAnnotations 들을 일괄 검증.
            // 검증 실패 시 InvalidOperationException 을 던져 비즈니스(<see cref="ExecuteCoreAsync"/>) 진입을 막고 catch 에서 FAILED 마감.
            ValidateParam(args);

            // [Submit 로그] 검증을 통과한 page-level 파라미터를 그대로 한 줄 남겨, 동일 LogId 에 대한 재현 가능한 입력값을 보존한다.
            // {@Param} 의 '@' 는 Serilog destructuring 지시자로, ToString() 이 아닌 객체 구조 그대로 직렬화한다.
            Logger.LogInformation(
                AppLog.Log("[{Domain}] {Job} Submit | Param={@Param}"),
                args.DomainCode, JobName, Param);

            // [상태 전이: ENQUEUED → RUNNING] race 결과를 outcome 으로 받아 정상 race(DEPRECATED) 만 조용히 종료시키고,
            // 그 외 비정상 상태(row 없음 / SUCCESS / FAILED / 다른 워커의 RUNNING 등) 는 <see cref="JobLogRepository.MarkRunningOrThrow"/>
            // 내부에서 InvalidOperationException 으로 던져 본 catch 로 떨어진다.
            //
            // AlreadyDeprecated 분기를 정상 종료로 두는 이유: sweeper 가 timeout 등으로 이미 마감한 row 라
            // 운영자가 알아야 할 새로운 사건이 아니다(알림 노이즈 방지). 그 외 race 는 dispatcher/워커 정합성 깨짐
            // 신호이므로 알림 받아야 한다 — 그래서 throw 경로로 일원화한다.
            if (Logs.MarkRunningOrThrow(args.LogId) == MarkRunningOutcome.AlreadyDeprecated)
            {
                Logger.WarnLogJobContext(args,
                    $"{JobName} skipped — row already DEPRECATED (sweeper race-loss)");
                return;
            }

            // [테스트 path 게이트] JobArgs.IsTest=true 면 ExecuteCoreAsync 호출하지 않고 RUNNING → SUCCESS 로 마감.
            //   - Dispatcher 가 (Domain/Group/Brand TEST_YN) OR (수동 runAs=test) 의 OR 결과로 IsTest 를 채워서 보냄.
            //   - 본 게이트는 스케줄링/디스패치/페이로드 파이프라인은 운영 트래픽 그대로 검증하되 leaf 비즈니스 부작용만 차단한다.
            //   - 마감은 <see cref="JobLogRepository.TryMarkSuccessAsTestSkipped"/> 로 위임 — ERROR_MESSAGE 에 "Skipped: test mode" 마커.
            //   - linked CTS / 비즈니스 호출 전이라 자원 할당도 발생하지 않음.
            if (args.IsTest)
            {
                Logger.LogJobContext(args, $"{JobName} skipped business — test mode (IsTest=true)");

                // [테스트 훅] ExecuteCoreAsync 는 호출하지 않되, 파생 Job 이 override 한 테스트 전용 로직
                // (ExecuteTestCoreAsync, 기본 no-op) 은 기존 try/catch 안에서 실행한다 — 여기서 예외가 나면
                // 동일 catch 로 떨어져 FAILED 마감 + 알림까지 운영 경로와 동일하게 탄다.
                // 운영 path 와 동일하게 shutdown + timeout 합성 토큰을 만들어 흘린다.
                using var testTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(args.TimeoutSec));
                using var testLinkedCts  = CancellationTokenSource.CreateLinkedTokenSource(ct, testTimeoutCts.Token);
                await ExecuteTestCoreAsync(args, testLinkedCts.Token).ConfigureAwait(false);

                SafeMarkSuccessAsTestSkipped(args);
                return;
            }

            // [Linked CancellationToken 합성] 두 개의 취소 신호 — (1) Hangfire 가 주입한 worker shutdown / job 삭제 token,
            // (2) JobArgs.TimeoutSec 기반 wall-clock timeout — 을 하나로 묶어 <see cref="ExecuteCoreAsync"/> 의
            // leaf 호출(DB / HTTP / gRPC) 까지 그대로 전달한다.
            // 둘 중 어느 쪽이 먼저 trigger 되어도 linkedCts.Token 이 즉시 cancel 되며, 양쪽 using 으로 누수 없이 정리된다.
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(args.TimeoutSec));
            using var linkedCts  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            // [비즈니스 실행] 파생 Job 의 실제 로직 호출. 예외는 본 메서드의 단일 catch 로 떨어져 FAILED 마감으로 이어진다.
            // ConfigureAwait(false): worker thread 의 SynchronizationContext 로 복귀할 필요가 없으므로 captured context 를 끊어 thread-hop 오버헤드를 줄인다.
            await ExecuteCoreAsync(args, linkedCts.Token).ConfigureAwait(false);

            // [성공 경로] 비즈니스가 예외 없이 끝났다면 RUNNING → SUCCESS 전이.
            // <see cref="SafeMarkSuccess"/> 는 내부의 모든 예외를 swallow — 비즈니스는 이미 성공했으므로 호출자에게 throw 하지 않는다.
            // 상태 컬럼이 어떤 이유로든 SUCCESS 로 못 옮겨졌다면 sweeper 가 후속 정리(stale RUNNING) 한다.
            SafeMarkSuccess(args);
            Logger.LogJobContext(args, $"{JobName} succeeded");
        }
        catch (BusinessException bex)
        {
            // [업무 예외 경로] 외부 서비스/프로시저가 정상 응답으로 돌려준 업무 실패(<see cref="BusinessException"/>).
            // 시스템 예외 경로와 마감 절차는 동일하되(<see cref="HandleFailureAsync"/>), 기본 메일은 업무용 라우팅
            // (브랜드 추가 메일 + 개발팀)으로 발송된다(isBusiness=true). 별도 catch 로 분리해 둔 이유는
            // 향후 업무/시스템 예외의 처리 분기가 갈릴 때 이 블록만 손대면 되도록 하기 위함이다.
            await HandleFailureAsync(args, bex, isBusiness: true).ConfigureAwait(false);

            // [원본 예외 rethrow] stack trace 보존. (rethrow 정책은 시스템 예외와 동일)
            ExceptionDispatchInfo.Capture(bex).Throw();
        }
        catch (Exception ex)
        {
            // [시스템 예외 경로] 역직렬화·검증·MarkRunningOrThrow·인프라 실패 등 업무 실패가 아닌 모든 예외.
            // 마감 절차는 <see cref="HandleFailureAsync"/> 로 일원화하며 기본 메일은 개발팀에만 발송(isBusiness=false).
            await HandleFailureAsync(args, ex, isBusiness: false).ConfigureAwait(false);

            // [원본 예외 rethrow] Hangfire 의 retry/dead-letter 파이프라인이 동작하도록 호출자에게 예외를 그대로 던진다.
            // <see cref="ExceptionDispatchInfo.Capture"/>(ex).Throw() 는 원본 throw 지점의 stack trace 를 그대로 보존한다 —
            // 단순 `throw ex;` 는 현재 라인 기준으로 trace 가 잘려 원인 추적이 어려워지므로 반드시 이 패턴을 유지할 것.
            ExceptionDispatchInfo.Capture(ex).Throw();
        }
    }

    /// <summary>
    /// <see cref="JobArgs.PayloadJson"/> 을 <typeparamref name="TParam"/> 으로 역직렬화. 빈 문자열/공백
    /// 또는 deserialize 결과가 null 이면 <see cref="InvalidOperationException"/> 으로 즉시 fail-fast —
    /// <see cref="ExecuteAsync"/> 의 단일 catch 가 받아 ENQUEUED → FAILED 로 끊는다.
    /// </summary>
    private static TParam DeserializePayload(JobArgs args)
    {
        if (string.IsNullOrWhiteSpace(args.PayloadJson))
            throw new InvalidOperationException(
                $"[{args.DomainCode}] PayloadJson missing — {typeof(TParam).Name} required");

        return JsonSerializer.Deserialize<TParam>(args.PayloadJson, JsonOpts)
               ?? throw new InvalidOperationException(
                   $"[{args.DomainCode}] PayloadJson deserialize returned null");
    }

    /// <summary>
    /// <see cref="Param"/> 에 붙은 <see cref="ValidationAttribute"/> 들을 모두 검사. 하나라도 실패하면
    /// 모인 오류 메시지를 합쳐 <see cref="InvalidOperationException"/> 으로 던져 <see cref="ExecuteAsync"/>
    /// 의 단일 catch 에서 ENQUEUED → FAILED 로 끊는다. payload 도 함께 메시지에 포함해 운영 시
    /// 원인 추적이 가능하게 한다.
    ///
    /// <para>
    /// 검사 원리: System.ComponentModel.DataAnnotations 의 표준 메커니즘을 그대로 사용한다.
    /// (1) DTO(<typeparamref name="TParam"/>) 의 public instance property 들을 reflection 으로 enumerate,
    /// (2) 각 property 에 선언된 <see cref="ValidationAttribute"/> 파생 클래스(예: <see cref="RequiredAttribute"/>,
    /// <see cref="RangeAttribute"/>, <see cref="RegularExpressionAttribute"/>, <see cref="StringLengthAttribute"/>,
    /// 도메인 측 커스텀 attribute) 의 <c>IsValid(value, ctx)</c> 를 호출,
    /// (3) 실패한 케이스만 <see cref="ValidationResult"/> 로 누적해 results 에 담는 방식이다.
    /// 객체 자체에 <see cref="IValidatableObject"/> 가 구현되어 있으면 그 <c>Validate</c> 도 함께 호출된다.
    /// </para>
    /// </summary>
    private void ValidateParam(JobArgs args)
    {
        // [ValidationContext 생성] 검사 대상 인스턴스(Param) 를 감싸 attribute 들에게 전달할 컨텍스트 객체.
        // ValidationAttribute.IsValid(value, ctx) 시그니처가 이 컨텍스트를 통해 "어떤 객체의 어떤 멤버를 검사 중인지",
        // "어떤 DI 서비스가 필요한지(ctx.GetService)" 를 알아낼 수 있다. serviceProvider/items 는 지금 필요 없으므로 null 로 둔다.
        // 주의: 본 컨텍스트는 Param 인스턴스 reference 만 보관할 뿐 deep clone 하지 않으므로 검사 도중 Param 을 mutate 해서는 안 된다.
        var ctx = new ValidationContext(Param);

        // [결과 버퍼] TryValidateObject 가 실패 케이스만 누적해 채워줄 리스트. 성공 시에는 비어 있는 상태로 남는다.
        // ValidationResult 1개 = "어떤 멤버(MemberNames) 가 어떤 사유(ErrorMessage) 로 실패했는가" 의 한 줄.
        var results = new List<ValidationResult>();

        // [핵심 호출] System.ComponentModel.DataAnnotations.Validator 의 정적 진입점.
        //   - 1st arg: 검사 대상 인스턴스 (Param)
        //   - 2nd arg: 컨텍스트 (ctx)
        //   - 3rd arg: 실패 결과를 받아갈 컬렉션 (results) — 호출 후 mutate 됨
        //   - validateAllProperties: true
        //       false 면 [Required] 만 검사하고 종료한다(.NET 기본 동작 — required 가 통과 못 하면 나머지는 의미 없다는 가정).
        //       true 로 강제해 [Required] 가 통과한 뒤에도 [Range]/[StringLength]/[Regex] 등 나머지 attribute 까지 모두 돌린다.
        //       운영 관점에서 "한 번에 모든 위반 사항을 모아 보고" 가 가능해져 재시도 비용을 줄인다.
        // return 값 true = 모든 attribute 통과. 이 경로에서는 results 가 비어 있고 즉시 메서드를 종료한다.
        if (Validator.TryValidateObject(Param, ctx, results, validateAllProperties: true))
            return;

        // [실패 메시지 합성] 누적된 ValidationResult 들을 "; " 로 한 줄에 이어붙여 사람이 읽기 쉬운 단일 문자열로 변환.
        // ErrorMessage 가 비어 있는 케이스(커스텀 attribute 가 메시지를 지정하지 않았을 때) 는 "(unspecified)" placeholder 로 대체해
        // 운영 로그에서 "왜 실패했는지 모르는 빈 칸" 이 나오지 않도록 방어한다.
        var detail = string.Join("; ",
            results.Select(r => r.ErrorMessage ?? "(unspecified)"));

        // [Fail-fast throw] InvalidOperationException 으로 escalation.
        // 메시지 컴포지션:
        //   - [{DomainCode}]      어느 도메인의 Job 인지 (TRACE/RETURN/RECALL …)
        //   - {TParam 단순 클래스명} 어떤 DTO 가 깨졌는지 (TraceFireParam 등)
        //   - {detail}             실제 위반 사항 합본
        //   - | payload={원본 JSON} 원인 파악을 위한 원본 입력 보존 — Dispatcher 측 직렬화 버그까지 추적 가능하게 한다.
        // 본 예외는 ExecuteAsync 의 단일 catch 로 떨어져 ENQUEUED → FAILED 로 마감되고 운영자 알림이 발송된 뒤,
        // Hangfire 의 retry/dead-letter 파이프라인에 그대로 rethrow 된다.
        throw new InvalidOperationException(
            $"[{args.DomainCode}] {typeof(TParam).Name} validation failed: {detail} | payload={args.PayloadJson}");
    }

    /// <summary>
    /// RUNNING → SUCCESS 전이를 시도하되 어떤 예외도 호출자로 전파하지 않는다.
    /// 0 rows(race-loser) 인 경우 warning, 예외인 경우 error 로그만 남기며 — 비즈니스는 이미
    /// 성공했으므로 상태 컬럼 불일치는 sweeper 가 후속 정리한다.
    /// <see cref="JobLogRepository.TryMarkSuccess"/>.
    /// </summary>
    private void SafeMarkSuccess(JobArgs args)
    {
        try
        {
            if (!Logs.TryMarkSuccess(args.LogId))
                Logger.WarnLogJobContext(args,
                    $"{JobName} success — row not in RUNNING (likely race-lost to sweeper)");
        }
        catch (Exception ex)
        {
            Logger.ErrorLogJobContext(args, ex,
                $"{JobName} TryMarkSuccess threw — log row may be stale, sweeper will reconcile");
        }
    }

    /// <summary>
    /// 테스트 path 스킵 마감 — RUNNING → SUCCESS 로 전이하되 ERROR_MESSAGE 에 "Skipped: test mode" 마커.
    /// <see cref="SafeMarkSuccess"/> 와 동일한 swallow 정책 — 비즈니스가 실행되지 않았으므로 호출자에 예외를 던지지 않음.
    /// </summary>
    private void SafeMarkSuccessAsTestSkipped(JobArgs args)
    {
        try
        {
            if (!Logs.TryMarkSuccessAsTestSkipped(args.LogId))
                Logger.WarnLogJobContext(args,
                    $"{JobName} test-skip — row not in RUNNING (likely race-lost to sweeper)");
        }
        catch (Exception ex)
        {
            Logger.ErrorLogJobContext(args, ex,
                $"{JobName} TryMarkSuccessAsTestSkipped threw — sweeper will reconcile");
        }
    }

    /// <summary>
    /// RUNNING → FAILED 전이를 시도하되 어떤 예외도 호출자로 전파하지 않는다. 본 메서드 안에서 발생한
    /// logging 단계 예외도 swallow — 호출자(<see cref="ExecuteAsync"/>) 는 원본 비즈니스 예외를
    /// <see cref="ExceptionDispatchInfo"/> 로 rethrow 해야 하므로 원본 정보를 절대 덮지 않는다.
    /// <see cref="JobLogRepository.TryMarkFailed"/>.
    /// </summary>
    private void SafeMarkFailed(JobArgs args, Exception ex)
    {
        try
        {
            if (!Logs.TryMarkFailed(args.LogId, ex))
                Logger.WarnLogJobContext(args,
                    $"{JobName} fail — row not in RUNNING (likely race-lost to sweeper)");
        }
        catch (Exception logEx)
        {
            Logger.ErrorLogJobContext(args, logEx,
                $"{JobName} TryMarkFailed threw — original exception preserved for rethrow");
        }
    }

    /// <summary>
    /// 두 catch 블록이 공유하는 실패 마감 절차. 업무/시스템 예외 모두 동일한 단계를 밟는다:
    ///   1) ERROR 레벨 로그
    ///   2) ENQUEUED/RUNNING → FAILED 상태 전이 (<see cref="SafeMarkFailed"/>)
    ///   3) 커스텀 훅 (<see cref="OnExceptionAsync"/>) — 기본 no-op, 파생 Job 이 필요 시 override
    ///   4) 시스템 기본 실패 메일 발송 (<see cref="SafeNotify"/>)
    /// 3·4 는 독립적이다 — 메일은 베이스의 시스템 기본 동작으로 항상 시도되고, 훅은 그와 별개로 호출된다.
    /// 모든 단계는 Safe* 래퍼라 자체 예외를 swallow 하며, 원본 예외 rethrow 를 절대 방해하지 않는다.
    /// rethrow 자체는 각 catch 블록이 <see cref="ExceptionDispatchInfo"/> 로 수행한다.
    /// </summary>
    /// <param name="args">실패한 Job 의 페이로드.</param>
    /// <param name="ex">catch 가 잡은 원본 예외.</param>
    /// <param name="isBusiness">
    /// 업무 예외(<see cref="BusinessException"/>) 여부. 기본 메일 수신자 라우팅에 사용 —
    /// true 면 브랜드 추가 메일 + 개발팀, false 면 개발팀만.
    /// </param>
    private async Task HandleFailureAsync(JobArgs args, Exception ex, bool isBusiness)
    {
        Logger.ErrorLogJobContext(args, ex, $"{JobName} failed");
        SafeMarkFailed(args, ex);
        await SafeOnExceptionAsync(args, ex).ConfigureAwait(false);
        SafeNotify(args, ex, isBusiness);
    }

    /// <summary>
    /// Job 실패 시 catch 에서 호출되는 커스텀 훅. 기본 구현은 no-op — 실패 메일은 베이스가
    /// <see cref="SafeNotify"/> 로 별도 처리하므로 본 훅은 순수하게 파생 Job 의 추가 로직
    /// (보상 트랜잭션, 추가 알림 채널, 상태 정리 등) 을 위한 확장 지점이다.
    ///
    /// <para>
    /// override 해도 기본 실패 메일 발송에는 영향을 주지 않는다. 본 훅에서 던진 예외는
    /// <see cref="SafeOnExceptionAsync"/> 가 swallow 하여 원본 예외 rethrow 를 방해하지 않는다.
    /// </para>
    /// </summary>
    /// <param name="args">실패한 Job 의 페이로드.</param>
    /// <param name="ex">catch 가 잡은 원본 예외.</param>
    /// <param name="ct">취소 토큰.</param>
    protected virtual Task OnExceptionAsync(JobArgs args, Exception ex, CancellationToken ct)
        => Task.CompletedTask;

    /// <summary>
    /// <see cref="OnExceptionAsync"/> 커스텀 훅을 호출하되 그 안에서 발생한 예외는 swallow.
    /// 훅 장애가 원본 예외 rethrow 를 막아서는 안 되므로 logging 만 남긴다.
    /// </summary>
    private async Task SafeOnExceptionAsync(JobArgs args, Exception ex)
    {
        try
        {
            await OnExceptionAsync(args, ex, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception hookEx)
        {
            Logger.ErrorLogJobContext(args, hookEx,
                $"{JobName} OnExceptionAsync threw — original exception preserved for rethrow");
        }
    }

    /// <summary>
    /// 시스템 기본 실패 메일 발송. <see cref="INotifier"/> 로 알림을 enqueue 하되 발송 자체의 예외는 swallow.
    /// <paramref name="isBusiness"/> 로 수신자 라우팅을 결정하고, 실제 발송 여부는
    /// <see cref="JobArgs.MailSend"/> 게이트가 NotificationJob 에서 최종 판단한다.
    /// </summary>
    /// <param name="args">실패한 Job 의 페이로드.</param>
    /// <param name="ex">catch 가 잡은 원본 예외 — 메일 본문 구성에 사용.</param>
    /// <param name="isBusiness">업무 예외 여부 — true 면 브랜드 추가 메일 + 개발팀, false 면 개발팀만.</param>
    private void SafeNotify(JobArgs args, Exception ex, bool isBusiness)
    {
        try
        {
            // 구조화된 실패 컨텍스트만 넘긴다 — 그룹별 본문(평문) 구성은 알림 계층(NotificationJob)의 책임.
            Notifier.Send(new NotificationRequest
            {
                Subject    = $"[{args.DomainCode}/{args.BrandCode}] {JobName} failed",
                JobName    = JobName,
                DomainCode = args.DomainCode,
                BrandCode  = args.BrandCode,
                CenterCode = args.CenterCode,
                LogId      = args.LogId,
                Requester  = args.Requester,
                IsTest     = args.IsTest,
                ErrorType  = ex.GetType().Name,
                IsBusiness = isBusiness,
                Message    = ex.Message,
                StackTrace = ex.StackTrace,
                Dump       = (ex as BusinessException)?.Dump,
                SystemMailSend = args.SystemMailSend,
                BrandMailSend  = args.BrandMailSend,
            });
        }
        catch (Exception notifyEx)
        {
            Logger.ErrorLogJobContext(args, notifyEx,
                $"{JobName} Notifier.Send threw — original exception preserved for rethrow");
        }
    }
}
