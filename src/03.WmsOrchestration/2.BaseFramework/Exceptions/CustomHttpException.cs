namespace Portfolio.WmsOrchestration.Exceptions
{
    public class CustomHttpException : Exception
    {
        public int status { get; private set; }
        public CustomHttpException(int statusCode, string title) : base(title)
        {
            status = statusCode;
        }
    }
}
