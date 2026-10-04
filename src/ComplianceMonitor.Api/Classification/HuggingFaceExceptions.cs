namespace ComplianceMonitor.Api.Classification;

/// <summary>
/// A failed call to Hugging Face. Messages carry the upstream status code only,
/// never the token, the request body or the response body.
/// </summary>
public abstract class HuggingFaceException : Exception
{
    protected HuggingFaceException(string message, int? upstreamStatusCode, Exception? innerException)
        : base(message, innerException)
    {
        UpstreamStatusCode = upstreamStatusCode;
    }

    /// <summary>The HTTP status Hugging Face returned, or null when there was no response.</summary>
    public int? UpstreamStatusCode { get; }
}

/// <summary>Worth retrying later: timeout, 408, 429, 5xx, network failure.</summary>
public sealed class HuggingFaceTransientException : HuggingFaceException
{
    private HuggingFaceTransientException(
        string message, int? upstreamStatusCode, bool isTimeout, TimeSpan? retryAfter, Exception? innerException)
        : base(message, upstreamStatusCode, innerException)
    {
        IsTimeout = isTimeout;
        RetryAfter = retryAfter;
    }

    public bool IsTimeout { get; }

    /// <summary>The upstream Retry-After delay, when Hugging Face sent one.</summary>
    public TimeSpan? RetryAfter { get; }

    public static HuggingFaceTransientException Timeout(int timeoutSeconds, Exception? innerException) =>
        new($"Hugging Face did not respond within {timeoutSeconds} s.", null, isTimeout: true, null, innerException);

    public static HuggingFaceTransientException Unreachable(Exception innerException) =>
        new("Hugging Face could not be reached.", null, isTimeout: false, null, innerException);

    public static HuggingFaceTransientException FromStatus(int statusCode, TimeSpan? retryAfter) =>
        new($"Hugging Face returned HTTP {statusCode}.", statusCode, isTimeout: false, retryAfter, null);
}

/// <summary>Retrying will not help: 400, 401, 402, 403, other 4xx, or a response we cannot read.</summary>
public sealed class HuggingFacePermanentException : HuggingFaceException
{
    private HuggingFacePermanentException(string message, int? upstreamStatusCode)
        : base(message, upstreamStatusCode, null)
    {
    }

    public bool IsMalformedResponse => UpstreamStatusCode is null;

    public static HuggingFacePermanentException FromStatus(int statusCode) =>
        new($"Hugging Face returned HTTP {statusCode}.", statusCode);

    public static HuggingFacePermanentException Malformed(string reason) =>
        new($"Hugging Face returned a response that could not be used: {reason}.", null);
}
