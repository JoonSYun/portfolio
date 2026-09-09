namespace Portfolio.WmsOrchestration.Model
{
    /// <summary>
    /// 사용자 전체 요청단위 상태
    /// </summary>
    public enum JobScopeStatus
    {
        PENDING,
        PROCESSING,
        COMPLETED,
        FAILED,
        CANCELED,
        SUSPENDED,
        PARTIAL_SUCCESS
    }
}
