namespace Portfolio.UnifiedScheduler.Models;

/// <summary>
/// Dispatcher → DomainJob 간 전달되는 단일 페이로드.
/// Hangfire가 HangFire.Job.Arguments 컬럼에 JSON 직렬화하여 저장.
/// </summary>
public class JobArgs
{
    public string   DomainCode  { get; set; } = "";
    public string   BrandCode   { get; set; } = "";
    public string   CenterCode  { get; set; } = "";
    public string?  PayloadJson { get; set; }
    public string   LogId       { get; set; } = "";
    public DateTime BucketedDt { get; set; }
    public int      TimeoutSec  { get; set; }

    /// <summary>
    /// 큐 대기 폐기 한도(초) — enqueue 시점 스냅샷. <see cref="TimeoutSec"/> 은 잡이 시작된 후의
    /// 실행 한도이고, 이 값은 시작되기 전 큐에서 기다려도 되는 한도다. DeprecationFilter 가
    /// <see cref="BucketedDt"/> 기준 age 와 비교해 초과 시 실행 전 폐기한다.
    /// <c>0</c> = 무제한(폐기 안 함).
    /// </summary>
    public int      DeprecateSec { get; set; }

    /// <summary>
    /// 본 실행이 테스트 path 인지 여부. Dispatcher 가 (Domain/Group/Brand TEST_YN OR
    /// 수동 runAs=test) 의 OR 결과로 채워 enqueue 한다. JobBase 가 이 값이 true 면
    /// ExecuteCoreAsync 호출 없이 RUNNING → SUCCESS 로 마감.
    /// </summary>
    public bool     IsTest      { get; set; }

    /// <summary>
    /// 시스템(개발팀) 메일 게이트. <b>SCH_DOMAIN.MAIL_SEND='Y'</b> 만 본다.
    /// Dispatcher 가 채워 enqueue 하고, NotificationJob 이 false 면 개발팀 메일을 보내지 않는다.
    /// </summary>
    public bool     SystemMailSend { get; set; }

    /// <summary>
    /// 브랜드 담당자 메일 게이트. <b>SCH_DOMAIN.MAIL_SEND='Y' AND SCH_BRAND.MAIL_SEND='Y' AND
    /// SCH_DOMAIN_BRAND_OVERRIDE.MAIL_SEND='Y'</b> (3-레벨 AND). false 면 브랜드 추가 메일(SCH_BRAND_ADD_MAIL)을 보내지 않는다.
    /// BrandMailSend=true 면 도메인도 'Y' 이므로 <see cref="SystemMailSend"/> 도 항상 true.
    /// </summary>
    public bool     BrandMailSend  { get; set; }

    /// <summary>
    /// 실행 요청자. cron 자동 발사는 "Scheduler", Admin "Run now" 는 RunNowController 가
    /// JWT NameIdentifier claim 으로 채운다. JobLogRepository 가 그대로 SCH_JOB_LOG.REQUESTER 에 INSERT.
    /// </summary>
    public string?  Requester   { get; set; }
}
