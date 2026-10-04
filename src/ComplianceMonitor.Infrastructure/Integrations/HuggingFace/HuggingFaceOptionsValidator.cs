using Microsoft.Extensions.Options;

namespace ComplianceMonitor.Infrastructure.Integrations.HuggingFace;

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

        if (options.AttemptTimeoutSeconds < 1 || options.AttemptTimeoutSeconds > options.TimeoutSeconds)
        {
            failures.Add(
                $"{section}:{nameof(options.AttemptTimeoutSeconds)} must be between 1 and " +
                $"{section}:{nameof(options.TimeoutSeconds)}.");
        }



        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
