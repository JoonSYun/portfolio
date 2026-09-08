namespace Portfolio.UnifiedScheduler.Models;

/// <summary>
/// Thrown when a request is well-formed but violates a business rule —
/// "queue still referenced by N domains", "JobType not registered in
/// DomainJobRegistry", etc. Maps to HTTP 400 by default; pass
/// <see cref="StatusCodes.Status409Conflict"/> for resource-conflict cases.
/// </summary>
public sealed class BusinessRuleException : DomainException
{
    public override int StatusCode { get; }
    public override string Title  => StatusCode == StatusCodes.Status409Conflict ? "Conflict" : "Bad Request";

    public BusinessRuleException(string message, int statusCode = StatusCodes.Status400BadRequest)
        : base(message)
    {
        StatusCode = statusCode;
    }
}
