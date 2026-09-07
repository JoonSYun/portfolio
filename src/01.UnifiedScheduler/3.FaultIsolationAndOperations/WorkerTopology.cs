using Hangfire;
using Hangfire.SqlServer;

namespace Portfolio.UnifiedScheduler.FaultIsolationAndOperations;

/// <summary>
/// [담당업무 3] 장애 격리 — 큐·워커·알림을 서버 단위로 분리.
///
/// Hangfire 워커 서버 하나가 큐 하나만 소비한다. 도메인은 자기 큐로만 적재된다.
/// 따라서 한 도메인(큐)의 Job 이 멈추거나 폭주해도 다른 브랜드·도메인의 워커는 영향이 없고,
/// 알림은 전용 워커가 처리하므로 알림 장애가 실행을 막지 않는다.
///
///   [스케줄러 서버] Register/Sync/Fire 만 수행, 큐 소비 안 함
///   [워커 서버 A]  queue: wms-sync      ← ORDER_SYNC, STOCK_CLOSE …
///   [워커 서버 B]  queue: erp-interface ← RETURN_PICKUP …
///   [알림 서버]    queue: notify        ← 결과 알림만
/// </summary>
public static class WorkerTopology
{
    public static void AddSchedulerNode(IServiceCollection services, string connectionString)
    {
        services.AddHangfire(cfg => cfg.UseSqlServerStorage(connectionString, new SqlServerStorageOptions
        {
            SchemaName = "hangfire",
            QueuePollInterval = TimeSpan.FromSeconds(1),
            UseRecommendedIsolationLevel = true,
            DisableGlobalLocks = true
        }));
        // 스케줄러 노드는 BackgroundJobServer 를 띄우지 않는다 — 등록·발사만 한다.
    }

    /// <summary>워커 노드: 지정된 큐 하나만 소비. WorkerCount 는 큐 특성별로 다르게 준다.</summary>
    public static void AddWorkerNode(IServiceCollection services, string connectionString, string queue, int workerCount)
    {
        AddSchedulerNode(services, connectionString);
        services.AddHangfireServer(options =>
        {
            options.ServerName = $"{Environment.MachineName}:{queue}";
            options.Queues = new[] { queue };          // ← 격리 경계
            options.WorkerCount = workerCount;
            options.ShutdownTimeout = TimeSpan.FromMinutes(2);
        });
    }

    /// <summary>알림 전용 노드: notify 큐만. 실행 워커와 프로세스 자체가 다르다.</summary>
    public static void AddNotificationNode(IServiceCollection services, string connectionString) =>
        AddWorkerNode(services, connectionString, NotificationWorker.QueueName, workerCount: 2);
}
