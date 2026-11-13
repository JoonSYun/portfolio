namespace Portfolio.WmsOrchestration.Model
{
    /// VERY IMPORTENT : Checks Domain DB or other storage to determine prior processing status.
    /// Checks chunk idempotency to prevent duplicate processing.
    /// NONE : No prior processing found.
    /// SUCCESS : Chunk was already processed successfully.
    /// FAIL : Chunk processing previously failed.
    public enum IdempotencyResult
    {
        NONE,
        DUPLICATE,
        SUCCESS,
        FAIL
    }
}
