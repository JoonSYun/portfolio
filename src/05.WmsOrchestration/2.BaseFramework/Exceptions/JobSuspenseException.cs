namespace Portfolio.WmsOrchestration.Exceptions
{
    public class JobSuspenseException : Exception
    {
        public JobSuspenseException()
        {
        }

        public JobSuspenseException(string? message) : base(message)
        {
        }

        public JobSuspenseException(string? message, Exception? innerException) : base(message, innerException)
        {
        }
    }
}
