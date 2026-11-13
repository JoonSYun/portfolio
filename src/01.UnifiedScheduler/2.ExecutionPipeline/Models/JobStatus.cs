namespace Portfolio.UnifiedScheduler.Data;

/// <summary>
/// <c>SCH_JOB_LOG.STATUS</c> 허용 값. SQL CHECK 제약(<c>CK_SCH_RUN_STATUS</c>) 과 1:1 매핑 —
/// EF Core <c>HasConversion&lt;string&gt;()</c> 로 열거자 이름을 그대로 저장한다.
/// C# 네이밍 컨벤션(PascalCase) 을 따르지 않는 이유: DB 컬럼 wire value 와 동치성을 유지해
/// 변환 레이어에서 매핑 누락이 발생하지 않게 하기 위함.
/// </summary>
public enum JobStatus
{
    ENQUEUED,
    RUNNING,
    SUCCESS,
    FAILED,
    DEPRECATED,
    SKIPPED_DUP,
}
