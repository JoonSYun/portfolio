namespace Portfolio.UnifiedScheduler.Models;

/// <summary>
/// Base class for application-level exceptions whose HTTP status code is
/// determined by the type itself, not by an ad-hoc try/catch in the controller.
/// <see cref="Infrastructure.Middleware.GlobalExceptionHandler"/> inspects the
/// concrete subclass and emits an RFC 7807 ProblemDetails with the matching
/// <see cref="StatusCode"/>.
///
/// 컨트롤러는 이 계열 예외를 잡지 않고 그대로 던지면 됨 — 글로벌 핸들러가
/// 일관된 응답으로 변환한다.
/// </summary>
public abstract class DomainException : Exception
{
    public abstract int StatusCode { get; }
    public virtual string Title => "An error occurred";

    protected DomainException(string message) : base(message) { }
    protected DomainException(string message, Exception inner) : base(message, inner) { }
}
