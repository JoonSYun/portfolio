namespace Portfolio.UnifiedScheduler.Infrastructure;

/// <summary>
/// Job 실패 알림 채널. 도메인 잡 워커는 <see cref="Send"/> 호출 즉시 리턴하고(fire-and-forget)
/// 실제 발송은 알림 전용 큐의 <see cref="Jobs.NotificationJob"/> 이 수행한다.
/// </summary>
public interface INotifier
{
    /// <summary>실패 알림을 발송(enqueue)한다. 수신자 라우팅/게이트/본문 구성은 NotificationJob 이 결정.</summary>
    void Send(NotificationRequest request);
}

/// <summary>
/// 실패 알림 1건의 구조화된 컨텍스트. JobBase 의 catch 가 채워 <see cref="INotifier.Send"/> 로 넘긴다.
/// 본문(평문) 구성은 표현 책임이라 JobBase 가 아니라 <see cref="Jobs.NotificationJob"/> 이 본 필드로부터 만든다.
/// Hangfire 직렬화를 거쳐 NotificationJob 으로 전달되므로 parameterless ctor + 단순 get/set 프로퍼티로 둔다.
/// </summary>
public class NotificationRequest
{
    /// <summary>메일 제목.</summary>
    public string Subject { get; set; } = "";

    /// <summary>실패한 Job 이름(파생 타입 단순 클래스명).</summary>
    public string JobName { get; set; } = "";

    public string DomainCode { get; set; } = "";

    /// <summary>브랜드 코드 — BusinessException 일 때 SCH_BRAND_ADD_MAIL 조회 키.</summary>
    public string BrandCode { get; set; } = "";

    public string CenterCode { get; set; } = "";
    public string LogId      { get; set; } = "";

    /// <summary>실행 요청자(개발자 본문에만 노출).</summary>
    public string? Requester { get; set; }

    public bool IsTest { get; set; }

    /// <summary>예외 타입 단순 이름 (예: BusinessException / InvalidOperationException).</summary>
    public string ErrorType { get; set; } = "";

    /// <summary>
    /// 비즈니스 예외 여부. true 면 개발팀(전체 본문) + 브랜드 추가 메일(제한 본문),
    /// false 면 개발팀(전체 본문) 에만 발송.
    /// </summary>
    public bool IsBusiness { get; set; }

    /// <summary>예외 메시지.</summary>
    public string Message { get; set; } = "";

    /// <summary>예외 스택트레이스(개발자 본문에만 노출).</summary>
    public string? StackTrace { get; set; }

    /// <summary>
    /// 업무 실패 원본 데이터의 평문 덤프(<see cref="Models.BusinessException.Dump"/>). 개발자 본문에만 노출 —
    /// 브랜드 담당자 본문에는 싣지 않는다. 덤프가 없으면 null 이고 본문에서 섹션 자체가 빠진다.
    /// </summary>
    public string? Dump { get; set; }

    /// <summary>시스템(개발팀) 메일 게이트 — 도메인 MAIL_SEND 만. false 면 개발팀 메일 미발송.</summary>
    public bool SystemMailSend { get; set; }

    /// <summary>브랜드 담당자 메일 게이트 — 도메인 AND 브랜드 AND 오버라이드. false 면 브랜드 추가 메일 미발송.</summary>
    public bool BrandMailSend { get; set; }
}
