namespace Portfolio.WmsOrchestration.Exceptions
{
    [Serializable]
    public class DBLockException : Exception
    {
        public DBLockException()
        {
        }

        public DBLockException(string? message) : base(message)
        {
        }

        public DBLockException(string? message, Exception? innerException) : base(message, innerException)
        {
        }
    }

    
}