namespace Portfolio.UnifiedScheduler.Infrastructure;

/// <summary>
/// Hangfire 코어/워커/서버 동작 옵션. appsettings.json 의 "Hangfire" 섹션과 매핑.
/// </summary>
/// <remarks>
/// 카테고리 큐별 동시성은 <c>SCH_QUEUE.WORKER_COUNT</c> 가 진실의 원천이라
/// 이 옵션에는 글로벌 워커 수가 존재하지 않는다. Recurring 전용 큐만 appsettings 로 별도 제어.
/// <para/>
/// 외부화 제외 항목 (구조적 결합이라 코드에 고정):
/// <list type="bullet">
///   <item>ConnectionString 이름 (<c>BackGroundJob</c>)</item>
///   <item><see cref="Hangfire.CompatibilityLevel"/> (라이브러리 버전 핀)</item>
///   <item><see cref="Hangfire.SqlServer.SqlServerStorageOptions.UseRecommendedIsolationLevel"/></item>
///   <item><see cref="Hangfire.SqlServer.SqlServerStorageOptions.DisableGlobalLocks"/></item>
///   <item>Dashboard 경로 (<c>/hangfire</c>)</item>
///   <item><c>QueuePollInterval</c> — Hangfire 1.7+ signal/notify 도입 후 fallback 주기로 격하돼 운영 튜닝 가치 낮음</item>
/// </list>
/// <para/>
/// 각 섹션 default 값은 모두 Hangfire 라이브러리 기본값과 일치한다 — 명시적으로 박아두는 목적은
/// (1) 라이브러리 메이저 업데이트로 기본값이 바뀌어도 동작이 안 변하게 하고,
/// (2) appsettings.json 만 보고 운영 동작을 추적 가능하게 만드는 데 있다.
/// </remarks>
public class HangfireOptions
{
    /// <summary>워커 공통 정책 (재시도 등). 큐별 워커 수는 <c>SCH_QUEUE.WORKER_COUNT</c> 가 결정.</summary>
    public WorkerSection Worker { get; set; } = new();

    /// <summary>SQL Server 스토리지 동작 옵션.</summary>
    public StorageSection Storage { get; set; } = new();

    /// <summary>DB 행 보존 정책 — 상태별 TTL. <see cref="Hangfire.States.SucceededState"/> 등 정적 프로퍼티에 반영된다.</summary>
    public RetentionSection Retention { get; set; } = new();

    /// <summary><see cref="Hangfire.BackgroundJobServerOptions"/> 의 라이프사이클·관측성 관련 공통 옵션.</summary>
    public ServerSection Server { get; set; } = new();

    /// <summary>Recurring 전용 큐 (인프라 잡 + 도메인 RecurringJob 본체) 설정.</summary>
    public RecurringSection Recurring { get; set; } = new();

    /// <summary>
    /// 인프라 RecurringJob 3종(<c>_schedule_sync</c>·<c>_deprecation_sweeper</c>·<c>_jobLog_archive</c>) 의
    /// 발사 주기/타임존 설정. 큐 자체는 <see cref="RecurringSection"/> 이 결정하고, 여기서는 각 잡의 cron 만 통제.
    /// </summary>
    public InfraJobsSection InfraJobs { get; set; } = new();

    /// <summary>
    /// 실패 알림(메일/메신저) 전용 큐 + 발송 timeout. 도메인 잡 워커를 외부 채널 지연으로부터 격리하기 위함 —
    /// 도메인 잡은 알림을 enqueue 만 하고 즉시 종료, 실제 발송은 이 큐의 별도 워커가 처리한다.
    /// </summary>
    public NotifierSection Notifier { get; set; } = new();

    public class WorkerSection
    {
        /// <summary>
        /// <see cref="Hangfire.AutomaticRetryAttribute.Attempts"/>. 기본 0.
        /// <see cref="Jobs.JobBase"/> 가 SCH_JOB_LOG 상태 전이로 retry 를 직접 관리하므로
        /// 0 이 정상 — 0 이 아닌 값으로 바꾸면 상태 전이가 깨질 수 있다.
        /// 운영 사고 시 임시 비상조치 외에는 변경하지 말 것.
        /// </summary>
        public int RetryAttempts { get; set; } = 0;
    }

    public class StorageSection
    {
        /// <summary>
        /// 워커가 잡을 가져간 뒤 visibility 를 갱신하는 슬라이딩 타임아웃.
        /// 평균 잡 실행 시간보다 충분히 커야 함 — 작으면 같은 잡이 다른 워커에 중복 픽업된다.
        /// </summary>
        public TimeSpan SlidingInvisibilityTimeout { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Hangfire 가 SQL Server 로 보내는 batch 명령의 최대 대기 시간.
        /// <see cref="Jobs.JobLogArchiveJob"/> 같은 대량 작업 시 의미 있고, 일반 잡에는 거의 무관.
        /// </summary>
        public TimeSpan CommandBatchMaxTimeout { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>
        /// <see cref="Hangfire.SqlServer.SqlServerStorageOptions.JobExpirationCheckInterval"/>.
        /// ExpirationManager 가 <c>[HangFire].[Job]</c> 등에서 <c>ExpireAt &lt; UTCNOW</c> 인 행을
        /// DELETE 하는 주기. 이 값만큼 청소 지연이 발생할 수 있다 — 즉 실제 보존 시간은
        /// <see cref="RetentionSection.SucceededExpiration"/> + 최대 이 값 까지 늘어남.
        /// 기본 30분.
        /// </summary>
        public TimeSpan JobExpirationCheckInterval { get; set; } = TimeSpan.FromMinutes(30);

        /// <summary>
        /// 한 번의 ExpirationManager 사이클에서 DELETE 할 최대 행 수.
        /// 큰 백로그가 한 방에 청소될 때 SQL 락 점유 시간을 통제하는 안전장치. 기본 1000.
        /// 대량 백로그 청소 가속이 필요하면 5000~10000 권장.
        /// </summary>
        public int DeleteExpiredBatchSize { get; set; } = 1000;

        /// <summary>
        /// <c>[HangFire].[Counter]</c> 행을 <c>[HangFire].[AggregatedCounter]</c> 로 롤업하는 주기.
        /// <c>[Counter]</c> 비대 방지에 직접 관련 (대시보드 통계 INSERT 가 카운터 행을 양산함).
        /// 기본 5분.
        /// </summary>
        public TimeSpan CountersAggregateInterval { get; set; } = TimeSpan.FromMinutes(5);
    }

    public class RetentionSection
    {
        /// <summary>
        /// <see cref="Hangfire.States.SucceededState.DefaultExpiration"/>.
        /// 잡이 Succeeded 상태로 전이될 때 <c>[Job]</c> 행에 박히는 TTL. 기본 1일.
        /// 이 솔루션은 별도로 <c>SCH_JOB_LOG</c> 가 7일 보관·아카이브를 갖고 있어
        /// Hangfire 쪽 보존은 짧게 가는 게 일관성 있음.
        /// </summary>
        public TimeSpan SucceededExpiration { get; set; } = TimeSpan.FromDays(1);

        /// <summary>
        /// <see cref="Hangfire.States.DeletedState.DefaultExpiration"/>.
        /// <see cref="Infrastructure.Hangfire.DeprecationFilter"/> 가 강등시킨 잡이나
        /// 운영자가 대시보드에서 Delete 한 잡의 TTL. 기본 1일.
        /// </summary>
        public TimeSpan DeletedExpiration { get; set; } = TimeSpan.FromDays(1);
    }

    public class ServerSection
    {
        /// <summary>
        /// <see cref="Hangfire.BackgroundJobServerOptions.ShutdownTimeout"/>.
        /// 호스트 종료 신호 후 현재 실행 중인 잡이 graceful 종료할 수 있는 최대 시간.
        /// 이 시간을 초과하면 워커가 강제 cancel 되어 <c>SCH_JOB_LOG.STATUS=RUNNING</c> 인 채로 끊김.
        /// Hangfire 기본 15초는 이 솔루션처럼 장기 잡(JobLogArchive 등)이 있는 환경에 짧다 —
        /// 30~60초 권장.
        /// </summary>
        public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// <see cref="Hangfire.BackgroundJobServerOptions.ServerCheckInterval"/>.
        /// 살아있는 다른 서버들이 죽은 서버(<see cref="ServerTimeout"/> 동안 heartbeat 없는)를
        /// 발견·정리하는 주기. 기본 5분.
        /// </summary>
        public TimeSpan ServerCheckInterval { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>
        /// <see cref="Hangfire.BackgroundJobServerOptions.ServerTimeout"/>.
        /// 서버가 이 시간 동안 heartbeat 를 갱신 못하면 죽은 걸로 간주.
        /// 죽었다고 판단된 서버가 fetch 해둔 잡은 invisibility 가 풀려 다른 워커가 재픽업.
        /// 기본 5분.
        /// </summary>
        public TimeSpan ServerTimeout { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>
        /// <see cref="Hangfire.BackgroundJobServerOptions.HeartbeatInterval"/>.
        /// 자기 자신의 heartbeat 갱신 주기. <see cref="ServerTimeout"/> 의 1/10 이하면 충분.
        /// 기본 30초.
        /// </summary>
        public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// <see cref="Hangfire.BackgroundJobServerOptions.SchedulePollingInterval"/>.
        /// Scheduled → Enqueued 전이를 위해 <c>[HangFire].[Set]</c> 을 폴링하는 주기.
        /// 도메인 RecurringJob 의 cron 정밀도가 분 단위라 15초로 충분. 기본 15초.
        /// </summary>
        public TimeSpan SchedulePollingInterval { get; set; } = TimeSpan.FromSeconds(15);
    }

    public class RecurringSection
    {
        /// <summary>
        /// Recurring 전용 큐 이름. 도메인 RecurringJob 본체(Dispatcher.Fire) 와 인프라 잡 3종
        /// (<c>_schedule_sync</c>·<c>_deprecation_sweeper</c>·<c>_jobLog_archive</c>) 가 이 큐로 발사된다.
        /// SCH_QUEUE 에는 등록되지 않으며, 전용 BackgroundJobServer 가 이 큐만 구독한다.
        /// </summary>
        public string QueueName { get; set; } = "recurring";

        /// <summary>
        /// Recurring 큐 전용 BackgroundJobServer 의 동시 실행 워커 수. 1 이면 모든 RecurringJob 이 순차 실행.
        /// Dispatcher.Fire 는 enqueue 만 하는 경량 작업이라 1 로도 분 단위 발사 부하를 감당하지만,
        /// _jobLog_archive 같은 장시간 작업과 동시 실행 여지를 두려면 2 이상을 권장.
        /// </summary>
        public int WorkerCount { get; set; } = 2;
    }

    public class InfraJobsSection
    {
        /// <summary>
        /// <c>_schedule_sync</c> — SCH_DOMAIN_SCHEDULE 변경분을 Hangfire RecurringJob 으로 reflect.
        /// 변경 감지 기반(SYNCED_UPD_DT 체크포인트)이라 매분 정도면 충분. 기본 매분/UTC.
        /// </summary>
        public InfraJobEntry ScheduleSync { get; set; } = new()
        {
            Cron     = "* * * * *",
            TimeZone = "UTC",
        };

        /// <summary>
        /// <c>_deprecation_sweeper</c> — RUNNING grace 초과한 SCH_JOB_LOG 행을 DEPRECATED 로 마킹.
        /// DeprecationFilter 와 책임 분리 — 필터는 픽업 직전 가드, sweeper 는 픽업 후 멈춘 잡 정리.
        /// 기본 매분/UTC.
        /// </summary>
        public InfraJobEntry DeprecationSweeper { get; set; } = new()
        {
            Cron     = "* * * * *",
            TimeZone = "UTC",
        };

        /// <summary>
        /// <c>_jobLog_archive</c> — SCH_JOB_LOG hot table → SCH_JOB_LOG_BK 로 retention 경과 row 이전.
        /// 운영 트래픽 가장 적은 시간대(KST 03:00)에 돌리는 게 관례. 기본 매일 03:00 / KST.
        /// </summary>
        public InfraJobEntry JobLogArchive { get; set; } = new()
        {
            Cron     = "0 3 * * *",
            TimeZone = "Korea Standard Time",
        };
    }

    public class NotifierSection
    {
        /// <summary>
        /// 알림 전용 큐 이름. <see cref="Infrastructure.HangfireNotifier"/> 가 이 큐로 enqueue 하고,
        /// 전용 BackgroundJobServer 가 이 큐만 구독해 도메인 잡 워커와 격리. 기본 "notifications".
        /// </summary>
        public string QueueName { get; set; } = "notifications";

        /// <summary>
        /// 알림 큐 전용 BackgroundJobServer 의 워커 수. 알림은 보통 short burst (분당 수십 건) 라
        /// 1 이면 충분하지만, 채널 응답이 느릴 때 적체 방지 여유로 2 권장.
        /// </summary>
        public int WorkerCount { get; set; } = 2;

        /// <summary>
        /// <see cref="Jobs.NotificationJob"/> 이 외부 채널(SMTP 등) 호출에 적용할 최대 대기 시간.
        /// 이 시간을 넘으면 발송을 포기하고 warning 로그만 남긴다 — 비즈니스 잡에 영향 없음.
        /// 기본 10초.
        /// </summary>
        public int TimeoutSec { get; set; } = 10;
    }

    /// <summary>
    /// 인프라 RecurringJob 한 개의 외부화 단위. cron 식 + 타임존 ID 2 필드.
    /// </summary>
    /// <remarks>
    /// <see cref="TimeZone"/> 은 Windows TimeZone ID 문자열 ("UTC", "Korea Standard Time" 등).
    /// <c>"UTC"</c> 는 특수 케이스로 <see cref="TimeZoneInfo.Utc"/> 로 직접 매핑되며,
    /// 그 외 값은 <see cref="TimeZoneInfo.FindSystemTimeZoneById"/> 로 resolve.
    /// </remarks>
    public class InfraJobEntry
    {
        /// <summary>5-필드 표준 cron 식. Hangfire 내부는 Cronos 로 파싱.</summary>
        public string Cron { get; set; } = "* * * * *";

        /// <summary>cron 해석 기준 타임존 ID. 기본 "UTC".</summary>
        public string TimeZone { get; set; } = "UTC";
    }
}
