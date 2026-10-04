namespace ComplianceMonitor.Application.Errors;

/// <summary>
/// A model call failed. Provider-neutral, so the HTTP layer maps it without knowing which provider is used.
/// The message never contains secrets or provider response bodies.
/// </summary>
public sealed class ModelGatewayException : Exception
{
    public ModelGatewayException(ModelGatewayFailureKind kind, string message, TimeSpan? retryAfter = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        RetryAfter = retryAfter;
    }

    public ModelGatewayFailureKind Kind { get; }

    public TimeSpan? RetryAfter { get; }
}
