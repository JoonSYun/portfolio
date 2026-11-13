using static Portfolio.WmsOrchestration.Model.Register;

namespace Portfolio.WmsOrchestration.Exceptions
{
    public class BusinessException : Exception
    {
        public readonly List<RegisterRespErrorList>? Errors;

        public BusinessException(string? message) : base(message)
        {
        }

        public BusinessException(string? message, List<RegisterRespErrorList>? errors) : base(message)
        {
            Errors = errors;
        }
    }
}
