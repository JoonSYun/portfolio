namespace Portfolio.UnifiedScheduler.Models;

/// <summary>
/// Contract every scheduler payload DTO must satisfy.
/// <c>BCODE</c> (brand / business-unit code) is required across every job —
/// concrete implementations annotate the property with <see cref="NotBlankAttribute"/>
/// and JobBase enforces it through <c>Validator.TryValidateObject</c>.
/// The admin UI reflects implementers of this interface to drive the
/// "Param DTO" dropdown and build the Monaco JSON schema.
/// </summary>
public interface ISchedulerParam
{
    string BCODE { get; }
}
