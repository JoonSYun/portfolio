using FluentValidation.Results;

namespace Portfolio.WmsOrchestration.Exceptions
{
    public class ValidationFailedException : Exception
    {
        public IReadOnlyList<ValidationFailure> Failures { get; }

        public ValidationFailedException(IEnumerable<ValidationFailure> failures)
            : base("Validation failed")
        {
            Failures = failures.ToList();
        }
    }
}