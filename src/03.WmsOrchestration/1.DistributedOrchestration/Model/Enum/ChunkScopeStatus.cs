namespace Portfolio.WmsOrchestration.Model
{
    /// <summary>
    /// 사용자의 요청을 분할한 Chunk 단위 상태
    /// </summary>
    public enum ChunkScopeStatus
    {
        PENDING,
        PROCESSING,
        COMPLETED,
        FAILED,
        PARTIAL_SUCCESS,
        CANCELLED,
        SUSPENDED        
    }
}
