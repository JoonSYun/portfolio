namespace Portfolio.UnifiedScheduler.ExecutionPipeline;

/// <summary>
/// [담당업무 2] 등록부터 알림까지 이어지는 실행 흐름을 하나의 8계층 파이프라인으로 표준화.
///
///   1 Register  도메인 스케줄을 DB에서 읽어 Hangfire RecurringJob 으로 등록 (도메인 단위)
///   2 Sync      설정 변경 감지 → 등록된 스케줄과 DB를 동기화 (무배포 반영)
///   3 Fire      Cron 도래 → 브랜드 fan-out (여기서만 브랜드 단위로 갈라진다)
///   4 Filter    운영 게이트 판정 (전역 일시정지 / 차단 / 검증 모드) — 3단 운영 제어
///   5 Enqueue   브랜드 실행 단위를 도메인 큐(=워커 서버)에 적재 — 장애 격리
///   6 Execute   공통 Job 베이스 라이프사이클 (로그·검증·상태전이·타임아웃·실패 마감)
///   7 Finalize  실행 이력 확정, 재시도 판정
///   8 Notify    알림 전용 워커로 결과 전달 (실행 워커와 분리)
/// </summary>
public enum PipelineStage
{
    Register = 1,
    Sync = 2,
    Fire = 3,
    Filter = 4,
    Enqueue = 5,
    Execute = 6,
    Finalize = 7,
    Notify = 8
}
