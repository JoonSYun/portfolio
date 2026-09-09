namespace Portfolio.WmsOrchestration.Exceptions
{
    public class SagaStateMissingException : Exception
    {

        public SagaStateMissingException()
        {
        }
        public SagaStateMissingException(string? message) : base(message)
        {
        }
        public SagaStateMissingException(string? message, Exception? innerException) : base(message, innerException)
        {
        }

    }
}
