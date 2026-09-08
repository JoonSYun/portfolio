namespace Portfolio.WmsOrchestration.Exceptions
{
    public class RedisLockException : Exception
    {
        public RedisLockException(string message) : base(message) { }
        public RedisLockException(string message, Exception innerException) : base(message, innerException) { }
    }
}
