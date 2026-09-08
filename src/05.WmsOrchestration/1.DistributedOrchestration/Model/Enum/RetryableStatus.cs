namespace Portfolio.WmsOrchestration.Model
{
    public enum RetryableStatus
    {
        RETRYABLE,
        EXCEED_MAX_RETRY_COUNT,
        DISCORD_JOB_STATUS,
        DISCORD_CHUNK_STATUS,
        NON_RETRYABLE
    }
}
