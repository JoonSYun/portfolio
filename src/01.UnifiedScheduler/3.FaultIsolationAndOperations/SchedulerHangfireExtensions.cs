using Dapper;
using Hangfire;
using Hangfire.SqlServer;
using Microsoft.Data.SqlClient;
using Serilog;
using Portfolio.UnifiedScheduler.Infrastructure.Database;

namespace Portfolio.UnifiedScheduler.Infrastructure;

/// <summary>
/// Hangfire 통합 부트스트랩 — storage / filters / per-queue servers / dashboard 한 파일에서 처리한다.
/// Program.cs 노출은 두 메서드:
/// <list type="number">
///   <item><see cref="AddSchedulerHangfire"/>          — 코어(스토리지·필터) + 큐별 워커 서버 + Recurring 전용 서버 등록</item>
///   <item><see cref="UseSchedulerHangfireDashboard"/> — 대시보드 라우트 매핑 (파이프라인 단계)</item>
/// </list>
/// </summary>
/// <remarks>
/// 큐별 동시성 정책:
/// <list type="bullet">
///   <item>카테고리 큐 — <c>SCH_QUEUE</c> 행 단위로 <see cref="BackgroundJobServerOptions.WorkerCount"/> = <c>WORKER_COUNT</c>.
///         기본 1 이면 큐 안에서 순차 실행.</item>
///   <item>Recurring 큐 — appsettings <c>Hangfire.Recurring</c> 가 진실의 원천. SCH_QUEUE 미등록.</item>
/// </list>
/// </remarks>
public static class SchedulerHangfireExtensions
{
    /// <summary>
    /// Hangfire 코어(스토리지·필터)와 큐별 워커 프로세스(서버) 일괄 등록.
    /// 운영 튜닝 값은 appsettings.json 의 "Hangfire" 섹션 → <see cref="HangfireOptions"/> 로 외부화돼 있다.
    /// 대시보드 매핑은 <see cref="UseSchedulerHangfireDashboard"/> 에서 별도 호출.
    /// </summary>
    /// <param name="services">DI 컨테이너.</param>
    /// <param name="configuration">
    /// <c>ConnectionStrings:BackGroundJob</c> 와 <c>Hangfire</c> 섹션을 읽는다.
    /// </param>
    /// <remarks>
    /// 큐 추가/제거 / WORKER_COUNT 변경은 모두 서버 재기동이 필요하다 — Hangfire 는
    /// <see cref="BackgroundJobServerOptions"/> 를 서버 생성 시점에 1회 캡처한 뒤 변경을 추적하지 않는다.
    /// </remarks>
    public static IServiceCollection AddSchedulerHangfire(
        this IServiceCollection services,
        IConfiguration         configuration)
    {
        // appsettings 의 "Hangfire" 섹션을 1회 바인딩 — 스토리지 옵션은 SP 빌드 전 즉시 필요하므로
        // GetSection().Get<T>() 로 즉시 머티리얼라이즈. 섹션 부재 시 기본값으로 채워진 새 인스턴스.
        var opt = configuration.GetSection("Hangfire").Get<HangfireOptions>() ?? new HangfireOptions();

        // 동일 옵션을 IOptions<HangfireOptions> 로도 등록 — RecurringJobRegistrar 등 런타임 소비처가 사용.
        services.Configure<HangfireOptions>(configuration.GetSection("Hangfire"));

        // 필터는 아래 storage-configuration 람다 안에서 GetRequiredService 로 resolve 되므로 DI 등록이 선행되어야 한다.
        services.AddSingleton<IdempotencyFilter>();
        services.AddSingleton<DeprecationFilter>();

        services.AddHangfire((sp, cfg) => cfg
            // Hangfire 1.8 데이터 형식 사용. 1.7 이전과 직렬화 호환성이 깨지므로 다운그레이드 시 전체 큐 비우기 필요.
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)

            // Job payload 의 Type 정보를 "ShortName, Assembly" 형태(버전·공개키 제외) 로 직렬화.
            // 어셈블리 리빌드/버전 변경 후에도 enqueue 된 잡이 역직렬화 가능 — 운영 배포 호환성 확보.
            .UseSimpleAssemblyNameTypeSerializer()

            // Newtonsoft.Json 권장 보안/성능 설정 적용. 핵심은 TypeNameHandling=None 으로 RCE 표면 차단.
            .UseRecommendedSerializerSettings()

            .UseSqlServerStorage(
                configuration.GetConnectionString(DBNames.BackGroundJob),
                new SqlServerStorageOptions
                {
                    // Hangfire 가 SQL 로 보내는 batch 커맨드의 최대 실행 대기. JobLogArchive 같은 대량 DELETE/INSERT
                    // 가 길어져도 SqlException 으로 떨어지지 않도록 여유 확보. 일반 잡 enqueue/dequeue 에는 무관.
                    CommandBatchMaxTimeout       = opt.Storage.CommandBatchMaxTimeout,

                    // 워커가 잡을 fetch 한 직후부터 visibility 가 가려지는 윈도우. 워커가 살아있는 동안 주기적으로
                    // 갱신되며, 워커 다운/네트워크 분단으로 갱신이 끊기면 이 시간 후 다른 워커가 재픽업.
                    // → 평균 잡 실행 시간보다 충분히 커야 중복 픽업이 안 일어남.
                    SlidingInvisibilityTimeout   = opt.Storage.SlidingInvisibilityTimeout,

                    // idle 워커가 큐를 polling 하는 주기. Hangfire 1.7+ 부터는 sp_getapplock 기반 signal/notify
                    // 가 우선 동작하므로 이 값은 fallback 주기로만 의미가 있다 (busy 시스템에서는 거의 발동 안 함).
                    QueuePollInterval            = TimeSpan.FromSeconds(15),

                    // Hangfire 기본 Serializable → ReadCommitted 로 격리 수준 강등. SQL Server 에서 lock 경합/
                    // deadlock 빈도를 크게 낮춤. SQL provider 에서는 켜는 것이 공식 권장.
                    UseRecommendedIsolationLevel = true,

                    // sp_getapplock 으로 거는 글로벌 advisory lock 비활성화. row-level lock 만으로 동시성을
                    // 처리해 멀티 워커 throughput 을 높인다. SlidingInvisibilityTimeout 기반 non-transactional
                    // fetch 와 짝지어 동작.
                    DisableGlobalLocks           = true,

                    // ExpirationManager 가 만료된 [Job]/[State]/[Hash]/[List]/[Set] 행을 DELETE 하는 주기.
                    // 실제 보존 시간 = Retention.SucceededExpiration + 최대 이 값(청소 지연 상한).
                    JobExpirationCheckInterval   = opt.Storage.JobExpirationCheckInterval,

                    // 한 사이클의 최대 DELETE 행 수. 큰 백로그 청소 시 락 점유 시간 통제용 안전장치.
                    DeleteExpiredBatchSize       = opt.Storage.DeleteExpiredBatchSize,

                    // [Counter] → [AggregatedCounter] 롤업 주기. [Counter] 비대 방지에 직접 관련.
                    CountersAggregateInterval    = opt.Storage.CountersAggregateInterval,
                })

            // Hangfire 자체 자동 재시도 비활성. JobBase 가 SCH_JOB_LOG 상태 전이로 retry 를 직접 관리하므로
            // 두 메커니즘이 동시에 돌면 상태가 어긋난다. 운영 사고 시 임시로 1-2 로 올리는 것 외에는 변경 금지.
            .UseFilter(new AutomaticRetryAttribute { Attempts = opt.Worker.RetryAttempts })

            // 클라이언트 단계 멱등 가드 (IClientFilter). 큐 INSERT 직전 SCH_JOB_LOG.LOG_ID UNIQUE INSERT 를
            // 시도하고 충돌 시 enqueue 자체를 취소. 같은 LogId 두 번 enqueue 되는 경합을 차단.
            .UseFilter(sp.GetRequiredService<IdempotencyFilter>())

            // 서버 단계 신선도 가드 (IElectStateFilter). Enqueued→Processing 전이 직전에 잡의 age(now - BucketedDt) 를 검사하고
            // JobArgs.DeprecateSec(큐 대기 한도) 초과면 DeletedState 로 강등 + SCH_JOB_LOG.STATUS=DEPRECATED.
            // DeprecateSec=0 은 무제한(폐기 안 함). 큐 적체로 한참 늦게 시작되는 잡의 결과 가치 없는 실행을 차단.
            .UseFilter(sp.GetRequiredService<DeprecationFilter>())

            // 상태 단위 보존 정책 (IApplyStateFilter). Succeeded/Deleted 전이 시 [Job].ExpireAt 에 박히는
            // TTL 을 Retention 섹션 값으로 덮어쓴다. Hangfire 1.8 은 정적 default 가 없어 필터 경유가 표준.
            .UseFilter(new StateExpirationFilter(
                opt.Retention.SucceededExpiration,
                opt.Retention.DeletedExpiration)));

        // ── 카테고리 큐별 서버 등록 ──────────────────────────────────────────────
        //
        // SCH_QUEUE(USE_YN='Y') 의 각 행마다 BackgroundJobServer 1개를 spawn.
        // 한 서버는 자기 큐 1개만 구독하고 WorkerCount 도 그 행의 값만 사용 →
        // 큐 사이에 동시성이 완전히 격리되며, WORKER_COUNT=1 인 큐는 자연스럽게 순차 실행.
        //
        // Hangfire OSS 의 WorkerCount 는 BackgroundJobServer 단위라, 글로벌 단일 서버로는
        // 큐별 상한을 둘 방법이 없다. "큐당 서버 1개" 가 이 제약을 우회하는 표준 패턴.
        //
        // DB 조회 실패 시에는 카테고리 서버를 spawn 하지 않고 경고만 — Recurring 서버는
        // 그대로 살아남아 _schedule_sync 가 다음 분 회복 시점에 RecurringJob 을 다시 밀어준다.
        //
        foreach (var queue in LoadQueueCatalog(configuration))
        {
            services.AddHangfireServer((sp, options) =>
            {
                options.Queues      = new[] { queue.QueueName };
                options.WorkerCount = queue.WorkerCount;
                options.ServerName  = BuildServerName(queue.QueueName);
                ApplyServerSection(options, opt.Server);
            });

            Log.Information(
                AppLog.Log("Hangfire queue server registered: queue={Queue} workers={Workers}"),
                queue.QueueName, queue.WorkerCount);
        }

        // ── Recurring 전용 서버 등록 ─────────────────────────────────────────────
        //
        // 도메인 RecurringJob 본체(Dispatcher.Fire) 와 인프라 잡 3종이 발사되는 큐.
        // 동시성은 appsettings 만 보고 결정 — SCH_QUEUE 와 무관.
        //
        services.AddHangfireServer((sp, options) =>
        {
            options.Queues      = new[] { opt.Recurring.QueueName };
            options.WorkerCount = opt.Recurring.WorkerCount;
            options.ServerName  = BuildServerName(opt.Recurring.QueueName);
            ApplyServerSection(options, opt.Server);
        });

        Log.Information(
            AppLog.Log("Hangfire recurring server registered: queue={Queue} workers={Workers}"),
            opt.Recurring.QueueName, opt.Recurring.WorkerCount);

        // ── 알림 전용 서버 등록 ──────────────────────────────────────────────────
        //
        // HangfireNotifier 가 enqueue 한 NotificationJob 만 이 큐에서 돈다. 도메인 잡 워커와 격리되어
        // 외부 채널(SMTP/메신저) 응답 지연이 비즈니스 throughput 에 영향 못 미치게 한다.
        // SCH_QUEUE 에는 등록되지 않으며 동시성은 appsettings 만 보고 결정.
        //
        services.AddHangfireServer((sp, options) =>
        {
            options.Queues      = new[] { opt.Notifier.QueueName };
            options.WorkerCount = opt.Notifier.WorkerCount;
            options.ServerName  = BuildServerName(opt.Notifier.QueueName);
            ApplyServerSection(options, opt.Server);
        });

        Log.Information(
            AppLog.Log("Hangfire notifier server registered: queue={Queue} workers={Workers}"),
            opt.Notifier.QueueName, opt.Notifier.WorkerCount);

        return services;
    }

    /// <summary>
    /// Hangfire 대시보드를 <c>/hangfire</c> 경로에 매핑. 접근 제어는 <see cref="IntranetAuthorizationFilter"/> 에 위임.
    /// </summary>
    /// <param name="app">호스트 빌드 후의 <see cref="WebApplication"/> 인스턴스.</param>
    public static WebApplication UseSchedulerHangfireDashboard(this WebApplication app)
    {
        app.UseHangfireDashboard("/hangfire", new DashboardOptions
        {
            Authorization = new[] { new IntranetAuthorizationFilter() },
        });

        return app;
    }

    /// <summary>
    /// 멀티 서버 환경에서 노드 식별자가 충돌하지 않도록 <c>MachineName#QueueName</c> 형태로 구성.
    /// Dashboard 의 Servers 탭에서 큐별 워커 상태를 시각적으로 분리해 볼 수 있다.
    /// </summary>
    private static string BuildServerName(string queueName)
        => $"Portfolio.UnifiedScheduler@{Environment.MachineName}#{queueName}";

    /// <summary>
    /// 카테고리 큐 서버와 Recurring 서버 양쪽에 공통으로 적용되는 라이프사이클·관측성 옵션.
    /// 큐별로 달리해야 할 이유가 없는 항목만 모았다 — Queues / WorkerCount / ServerName 은 각 람다에서 별도 설정.
    /// </summary>
    private static void ApplyServerSection(BackgroundJobServerOptions options, HangfireOptions.ServerSection section)
    {
        options.ShutdownTimeout         = section.ShutdownTimeout;
        options.ServerCheckInterval     = section.ServerCheckInterval;
        options.ServerTimeout           = section.ServerTimeout;
        options.HeartbeatInterval       = section.HeartbeatInterval;
        options.SchedulePollingInterval = section.SchedulePollingInterval;
    }

    /// <summary>
    /// SCH_QUEUE(USE_YN='Y') 행을 읽어 큐 이름·워커 수 페어로 반환.
    /// </summary>
    /// <remarks>
    /// DI 컨테이너 구축 이전 시점이라 EF Core 의 <c>IDbContextFactory</c> 를 안전하게 resolve 할 방법이 없다
    /// (BuildServiceProvider 호출은 ASP0000 경고를 띄우고 싱글톤 인스턴스를 분기시킨다).
    /// 그래서 connection string 직접 + Dapper 단일 쿼리로 간단히 읽는다 — 기동 시점 1회만 실행되는 코드라 충분.
    /// <para/>
    /// DB unreachable 상태에서도 기동이 막히지 않도록 예외를 삼키고 빈 목록을 반환한다 —
    /// Recurring 서버는 별도로 등록되므로 운영자 진단(/hangfire)·<c>_schedule_sync</c> 동작은 유지된다.
    /// </remarks>
    private static IReadOnlyList<QueueCatalogEntry> LoadQueueCatalog(IConfiguration configuration)
    {
        var connStr = configuration.GetConnectionString(DBNames.BackGroundJob);
        if (string.IsNullOrWhiteSpace(connStr))
        {
            Log.Warning(AppLog.Log("BackGroundJob connection string missing; no category queue servers will be spawned"));
            return Array.Empty<QueueCatalogEntry>();
        }

        try
        {
            using var conn = new SqlConnection(connStr);
            return conn.Query<QueueCatalogEntry>(@"
                SELECT QUEUE_NAME   AS QueueName,
                       WORKER_COUNT AS WorkerCount
                  FROM dbo.SCH_QUEUE
                 WHERE USE_YN = 'Y'
                 ORDER BY QUEUE_NAME").AsList();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, AppLog.Log("SCH_QUEUE load failed; no category queue servers will be spawned"));
            return Array.Empty<QueueCatalogEntry>();
        }
    }

    /// <summary>SCH_QUEUE 행 1개를 BackgroundJobServer 설정용으로 가볍게 투영한 record.</summary>
    private sealed record QueueCatalogEntry(string QueueName, int WorkerCount);
}
