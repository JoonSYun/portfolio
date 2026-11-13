namespace Portfolio.UnifiedScheduler.Models;

/// <summary>
/// Thrown when a requested entity is missing — repository miss, dispatcher
/// target row absent, etc. Maps to HTTP 404.
///
/// Two convenience constructors:
///   <c>new NotFoundException("Domain not found: ABC")</c>            ← raw message
///   <c>new NotFoundException("Domain", "ABC")</c>                    ← entity + key
/// </summary>
public sealed class NotFoundException : DomainException
{
    public override int StatusCode => StatusCodes.Status404NotFound;
    public override string Title  => "Not Found";

    public NotFoundException(string message) : base(message) { }
    public NotFoundException(string entity, string key) : base($"{entity} not found: {key}") { }
}
