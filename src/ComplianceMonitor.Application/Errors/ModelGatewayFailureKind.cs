namespace ComplianceMonitor.Application.Errors;

public enum ModelGatewayFailureKind
{
    /// <summary>The model did not answer in time.</summary>
    Timeout,

    /// <summary>Temporarily unavailable: overloaded, rate-limited, network failure, or our circuit breaker is open.</summary>
    Unavailable,

    /// <summary>The account's inference credits are used up.</summary>
    CreditsExhausted,

    /// <summary>The provider refused the request (for example, bad credentials).</summary>
    Rejected,

    /// <summary>The provider answered with something we could not use.</summary>
    InvalidResponse,

    /// <summary>The action and guideline together don't fit the model's input. The caller's to fix.</summary>
    InputTooLong,
}
