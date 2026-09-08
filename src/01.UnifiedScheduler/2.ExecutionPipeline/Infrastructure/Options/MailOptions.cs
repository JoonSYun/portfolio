namespace Portfolio.UnifiedScheduler.Infrastructure;

/// <summary>
/// 에러 메일 발송 설정. appsettings.json 의 "Mail" 섹션과 매핑.
/// 레거시 메일 모듈이 하드코딩하던 SMTP/계정/수신자를 외부화한다.
/// 비밀번호 등 민감값은 환경별 appsettings(Staging/Production) 또는 환경변수로 override 권장.
/// </summary>
public class MailOptions
{
    /// <summary>SMTP 서버 호스트. 예: smtp.office365.com.</summary>
    public string SmtpServer { get; set; } = "";

    /// <summary>SMTP 포트. office365 STARTTLS = 587.</summary>
    public int Port { get; set; } = 587;

    /// <summary>STARTTLS 사용 여부.</summary>
    public bool EnableSsl { get; set; } = true;

    /// <summary>발신자 주소. 예: master@slogis.co.kr.</summary>
    public string FromAddress { get; set; } = "";

    /// <summary>발신자 표시 이름(선택).</summary>
    public string FromDisplayName { get; set; } = "WS.CORE Scheduler";

    /// <summary>SMTP 인증 사용자명. 보통 <see cref="FromAddress"/> 와 동일.</summary>
    public string Username { get; set; } = "";

    /// <summary>SMTP 인증 비밀번호. 환경별 설정/환경변수로 주입 권장.</summary>
    public string Password { get; set; } = "";

    /// <summary>
    /// 개발팀(시스템 알림) 수신자 목록. 모든 에러 메일의 기본 수신자이며,
    /// BusinessException 인 경우 여기에 브랜드별 추가 이메일(SCH_BRAND_ADD_MAIL)이 더해진다.
    /// </summary>
    public List<string> ItTeamRecipients { get; set; } = new();
}
