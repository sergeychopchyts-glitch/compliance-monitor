namespace ComplianceMonitor.Infrastructure.Integrations.HuggingFace;

/// <summary>
/// Sits inside the retry pipeline and counts attempts on the request, so the client can log
/// how many attempts one classification took.
/// </summary>
internal sealed class AttemptCountingHandler : DelegatingHandler
{
    public static readonly HttpRequestOptionsKey<int> AttemptsKey = new("ComplianceMonitor.Attempts");

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Options.TryGetValue(AttemptsKey, out var attempts);
        request.Options.Set(AttemptsKey, attempts + 1);
        return base.SendAsync(request, cancellationToken);
    }
}
