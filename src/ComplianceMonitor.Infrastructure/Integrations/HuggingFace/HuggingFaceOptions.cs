namespace ComplianceMonitor.Infrastructure.Integrations.HuggingFace;

public sealed class HuggingFaceOptions
{
    public const string SectionName = "HuggingFace";

    public Uri BaseUrl { get; set; } = new("https://router.huggingface.co/hf-inference/models/");

    public string Model { get; set; } = "facebook/bart-large-mnli";

    /// <summary>Secret. Comes from user-secrets or the HuggingFace__ApiToken environment variable only.</summary>
    public string ApiToken { get; set; } = "";

    /// <summary>Total budget for one classification, including retries and backoff.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Budget for a single HTTP attempt; a slower attempt is cancelled and retried.</summary>
    public int AttemptTimeoutSeconds { get; set; } = 10;



    public Uri ModelUrl => new(new Uri(BaseUrl.AbsoluteUri.TrimEnd('/') + "/"), Model);
}
