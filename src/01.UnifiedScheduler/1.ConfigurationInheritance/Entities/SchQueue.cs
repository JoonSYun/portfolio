using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Portfolio.UnifiedScheduler.Data;

[Table("SCH_QUEUE")]
public class SchQueue
{
    [Key]
    [Column("QUEUE_NAME")] [MaxLength(30)]
    public string QueueName { get; set; } = "";

    [Column("DESCRIPTION")] [MaxLength(200)]
    public string? Description { get; set; }

    [Column("USE_YN")] [MaxLength(1)]
    public string UseYn { get; set; } = "Y";

    /// <summary>
    /// 이 큐 전용 BackgroundJobServer 의 동시 실행 워커 수. 기본 1 → 큐 내부 순차 실행.
    /// 기동 시점에 <see cref="Portfolio.UnifiedScheduler.Infrastructure.SchedulerHangfireExtensions"/> 가
    /// SCH_QUEUE 행 단위로 별도 서버를 spawn 하면서 이 값을 <c>BackgroundJobServerOptions.WorkerCount</c> 로 박는다.
    /// 운영 중 변경하려면 UPDATE 후 스케줄러 서버 재기동 필요.
    /// </summary>
    [Column("WORKER_COUNT")]
    public int WorkerCount { get; set; } = 1;

    [Column("REG_DT")] public DateTime RegDt { get; set; }
    [Column("UPD_DT")] public DateTime UpdDt { get; set; }
}
