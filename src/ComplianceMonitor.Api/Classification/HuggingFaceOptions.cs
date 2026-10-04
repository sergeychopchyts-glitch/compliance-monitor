using Microsoft.Extensions.Options;

namespace ComplianceMonitor.Api.Classification;

public sealed class HuggingFaceOptions
{
    public const string SectionName = "HuggingFace";

    public Uri BaseUrl { get; set; } = new("https://router.huggingface.co/hf-inference/models/");

    public string Model { get; set; } = "facebook/bart-large-mnli";

    /// <summary>Secret. Comes from user-secrets or the HuggingFace__ApiToken environment variable only.</summary>
    public string ApiToken { get; set; } = "";

    /// <summary>Total budget for one classification, including retries.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>A top score below this gives UNCLEAR with <see cref="DecisionSource.LowConfidence"/>.</summary>
    public double ConfidenceFloor { get; set; } = 0.5;

    public Uri ModelUrl => new(new Uri(BaseUrl.AbsoluteUri.TrimEnd('/') + "/"), Model);
}

/// <summary>
/// Validates <see cref="HuggingFaceOptions"/> at startup. Messages name the config key and never echo a value.
/// </summary>
public sealed class HuggingFaceOptionsValidator : IValidateOptions<HuggingFaceOptions>
{
    public const int MaxTimeoutSeconds = 90;

    public ValidateOptionsResult Validate(string? name, HuggingFaceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        var section = HuggingFaceOptions.SectionName;

        if (string.IsNullOrWhiteSpace(options.ApiToken))
        {
            failures.Add(
                $"{section}:{nameof(options.ApiToken)} is not configured. Set it with " +
                $"'dotnet user-secrets set {section}:{nameof(options.ApiToken)} <token>' " +
                $"or the {section}__{nameof(options.ApiToken)} environment variable.");
        }

        if (options.BaseUrl is null || !options.BaseUrl.IsAbsoluteUri || options.BaseUrl.Scheme != Uri.UriSchemeHttps)
        {
            failures.Add($"{section}:{nameof(options.BaseUrl)} must be an absolute https URL.");
        }

        if (string.IsNullOrWhiteSpace(options.Model))
        {
            failures.Add($"{section}:{nameof(options.Model)} is not configured.");
        }

        if (options.TimeoutSeconds is < 1 or > MaxTimeoutSeconds)
        {
            failures.Add($"{section}:{nameof(options.TimeoutSeconds)} must be between 1 and {MaxTimeoutSeconds}.");
        }

        if (!double.IsFinite(options.ConfidenceFloor) || options.ConfidenceFloor is < 0 or > 1)
        {
            failures.Add($"{section}:{nameof(options.ConfidenceFloor)} must be between 0 and 1.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
