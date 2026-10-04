namespace ComplianceMonitor.Api.RateLimiting;

/// <summary>
/// Concurrency limit for POST /analyze: each request holds a call to the external model for up to 30 s, so this
/// caps how many run at once (and how many may wait), protecting the model quota and this server's threads.
/// </summary>
public sealed class AnalyzeRateLimitOptions
{
    public const string SectionName = "RateLimiting:Analyze";
    public const string PolicyName = "analyze";

    public int PermitLimit { get; set; } = 4;

    public int QueueLimit { get; set; } = 2;
}
