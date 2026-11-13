// [담당업무 2·3] 알림 단계 — 알림 전용 큐에서 실행되는 발송 잡. 실행 워커는 외부 채널 응답을 절대 기다리지 않는다.

using Microsoft.Extensions.Options;
using Portfolio.UnifiedScheduler.Infrastructure;

namespace Portfolio.UnifiedScheduler.Jobs;

/// <summary>
/// 알림 전용 큐에서 실행되는 발송 잡. <see cref="HangfireNotifier"/> 가 enqueue 한 알림 요청을
/// 게이트/라우팅 판단 후 <see cref="SmtpNotifier"/> 로 흘리되,
/// <see cref="HangfireOptions.NotifierSection.TimeoutSec"/> 기반 linked CancellationToken 으로
/// 외부 채널 지연을 가둔다.
/// </summary>
/// <remarks>
/// <para>책임 분리</para>
/// <list type="bullet">
///   <item><see cref="HangfireNotifier"/> — INotifier 어댑터. enqueue 만 (즉시 리턴).</item>
///   <item><see cref="NotificationJob"/> (이 클래스) — 게이트 + 수신자 라우팅 + 라이프사이클 + timeout 합성.</item>
///   <item><see cref="SmtpNotifier"/> — 실제 SMTP 호출.</item>
/// </list>
/// 이 분리로 도메인 잡 워커는 절대 외부 채널 응답을 기다리지 않으며, 알림 자체도 타임아웃 박힘.
/// <para/>
/// 수신자 라우팅:
/// <list type="bullet">
///   <item><c>mailSend=false</c> → 발송하지 않음(도메인/브랜드 MAIL_SEND 게이트 OFF, 개발팀 포함 전부 차단).</item>
///   <item>개발팀(<see cref="MailOptions.ItTeamRecipients"/>) — 모든 알림의 기본 수신자.</item>
///   <item><c>isBusiness=true</c> → SCH_BRAND_ADD_MAIL 의 브랜드별 활성 이메일을 추가.</item>
/// </list>
/// </remarks>
public class NotificationJob
{
    private readonly SmtpNotifier                   _sender;
    private readonly BrandMailRepository            _brandMails;
    private readonly MailOptions                    _mail;
    private readonly HangfireOptions.NotifierSection _opt;
    private readonly ILogger<NotificationJob>       _logger;

    public NotificationJob(
        SmtpNotifier               sender,
        BrandMailRepository        brandMails,
        IOptions<MailOptions>      mailOptions,
        IOptions<HangfireOptions>  options,
        ILogger<NotificationJob>   logger)
    {
        _sender     = sender;
        _brandMails = brandMails;
        _mail       = mailOptions.Value;
        _opt        = options.Value.Notifier;
        _logger     = logger;
    }

    /// <summary>
    /// Hangfire worker 진입점. 게이트 통과 시 시스템 메일(개발팀, 전체 본문) 과
    /// 브랜드 메일(브랜드 담당자, 제한 본문) 을 <b>각각 독립적으로</b> 발송한다.
    /// 본문(평문) 은 본 메서드가 <paramref name="req"/> 의 구조화 필드로부터 그룹별로 구성한다
    /// (표현 책임을 JobBase 가 아닌 알림 계층이 가진다).
    /// 두 그룹의 수신자 중복 제거는 그룹 내에서만 수행하므로, 같은 주소가 양쪽에 등록돼 있으면
    /// 시스템 메일(전체) 과 브랜드 메일(제한) 을 모두 받는다.
    /// <paramref name="ct"/> 는 Hangfire 의 shutdown / job-delete 토큰이며, 여기에
    /// <c>NotifierSection.TimeoutSec</c> 기반 timeout 을 linked 로 합성해 sender 에 흘린다.
    /// </summary>
    public async Task SendAsync(NotificationRequest req, CancellationToken ct)
    {
        // [게이트 — 그룹별 독립]
        //   시스템(개발팀) 메일: SystemMailSend(도메인 MAIL_SEND) 만 본다.
        //   브랜드 담당자 메일: BrandMailSend(도메인 AND 브랜드 AND 오버라이드) + BusinessException + 브랜드코드.
        //   둘 다 꺼져 있으면 보낼 게 없다.
        if (!req.SystemMailSend && !req.BrandMailSend)
        {
            _logger.LogInformation(
                AppLog.Log("Notification skipped — MAIL_SEND off (system & brand). Domain={Domain} Brand={Brand} Subject={Subject}"),
                req.DomainCode, req.BrandCode, req.Subject);
            return;
        }

        // [수신자 라우팅 — 그룹별 독립]
        //   시스템 메일: SystemMailSend 일 때만 개발팀. 그룹 내에서만 중복 제거.
        //   브랜드 메일: BrandMailSend + BusinessException 일 때만 SCH_BRAND_ADD_MAIL. 그룹 내에서만 중복 제거.
        //   교차 중복은 제거하지 않는다 — 같은 주소가 양쪽에 있으면 두 메일을 모두 받게 한다(의도).
        var systemRecipients = req.SystemMailSend
            ? Dedup(_mail.ItTeamRecipients)
            : new List<string>();
        var brandRecipients  = req.BrandMailSend && req.IsBusiness && !string.IsNullOrWhiteSpace(req.BrandCode)
            ? Dedup(_brandMails.GetActiveBrandEmails(req.BrandCode))
            : new List<string>();

        if (systemRecipients.Count == 0 && brandRecipients.Count == 0)
        {
            _logger.LogWarning(
                AppLog.Log("Notification has no recipients — check Mail:ItTeamRecipients / SCH_BRAND_ADD_MAIL. Domain={Domain} Brand={Brand} Subject={Subject}"),
                req.DomainCode, req.BrandCode, req.Subject);
            return;
        }

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_opt.TimeoutSec));
        using var linkedCts  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        // 두 그룹을 독립적으로 발송 — 한쪽 실패가 다른 쪽 발송을 막지 않는다. 본문도 그룹별로 다르게 구성.
        await SendGroupAsync("system", req.Subject, BuildSystemBody(req), systemRecipients, timeoutCts, linkedCts.Token, ct).ConfigureAwait(false);
        await SendGroupAsync("brand",  req.Subject, BuildBrandBody(req),  brandRecipients,  timeoutCts, linkedCts.Token, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 개발자(개발팀) 용 전체 본문(평문). Job 컨텍스트 + Requester + 메시지 + StackTrace 까지 모두 포함하고,
    /// 업무 실패가 원본 덤프를 실어 보냈으면(<see cref="NotificationRequest.Dump"/>) 맨 뒤에 Dump 섹션을 덧붙인다 —
    /// 서버 로그를 뒤지지 않고 메일만으로 원인 행을 보기 위한 것이며, 브랜드 본문에는 넣지 않는다.
    /// </summary>
    private static string BuildSystemBody(NotificationRequest r)
    {
        var body =
            $"""
             Job        : {r.JobName}
             Domain     : {r.DomainCode}
             Brand      : {r.BrandCode}
             Center     : {r.CenterCode}
             LogId      : {r.LogId}
             Requester  : {r.Requester}
             IsTest     : {r.IsTest}
             ErrorType  : {r.ErrorType}{(r.IsBusiness ? " (Business)" : " (System)")}

             Message    : {r.Message}

             StackTrace :
             {r.StackTrace}
             """;

        return string.IsNullOrWhiteSpace(r.Dump)
            ? body
            : $"{body}{Environment.NewLine}{Environment.NewLine}Dump :{Environment.NewLine}{r.Dump}";
    }

    /// <summary>
    /// 브랜드 담당자용 제한 본문(평문). 브랜드에게 의미 있는 최소 정보만 — Brand/LogId/Message.
    /// </summary>
    private static string BuildBrandBody(NotificationRequest r) =>
        $"""
         Brand   : {r.BrandCode}
         LogId   : {r.LogId}
         Message : {r.Message}
         """;

    /// <summary>
    /// 한 수신자 그룹에 대해 메일 1건을 발송하되, 발송 자체의 예외(timeout 포함) 는 swallow 한다.
    /// 수신자가 없으면 아무것도 하지 않는다.
    /// </summary>
    private async Task SendGroupAsync(
        string                      group,
        string                      subject,
        string                      body,
        IReadOnlyCollection<string> recipients,
        CancellationTokenSource     timeoutCts,
        CancellationToken           linkedToken,
        CancellationToken           ct)
    {
        if (recipients.Count == 0) return;

        try
        {
            await _sender.SendAsync(subject, body, recipients, linkedToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // timeout (shutdown 아님) — warning 만 남기고 swallow. 비즈니스 잡에 전파될 경로 없음.
            _logger.LogWarning(
                AppLog.Log("Notification send timed out after {Sec}s. Group={Group} Subject={Subject}"),
                _opt.TimeoutSec, group, subject);
        }
        catch (Exception ex)
        {
            // 알림 잡 실패가 Hangfire FailedState 로 가지 않도록 swallow — retry/dead 누적 무의미.
            _logger.LogError(ex,
                AppLog.Log("Notification send failed. Group={Group} Subject={Subject}"),
                group, subject);
        }
    }

    /// <summary>공백 제거 + trim + 대소문자 무시 중복 제거. 그룹 내부 중복만 제거한다.</summary>
    private static List<string> Dedup(IEnumerable<string> emails) =>
        emails
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => e.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
