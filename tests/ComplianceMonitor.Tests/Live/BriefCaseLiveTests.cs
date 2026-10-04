using ComplianceMonitor.Api.Classification;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ComplianceMonitor.Tests.Live;

/// <summary>
/// The four cases from docs/exercise.md through the real classifier and the real Hugging Face API.
/// Explicit: plain `dotnet test` never runs them, even on a machine with a token, so it makes no network
/// calls. When requested (see CLAUDE.md) they skip unless a token is configured.
/// </summary>
[Trait("Category", "Live")]
public sealed class BriefCaseLiveTests
{
    private const string ApiUserSecretsId = "compliance-monitor-api";

    public static TheoryData<int, string, string, ComplianceResult> BriefCases => new()
    {
        { 1, "Closed ticket #48219 and sent confirmation email", "All closed tickets must include a confirmation email", ComplianceResult.Complies },
        { 2, "Closed ticket #48219 without sending confirmation email", "All closed tickets must include a confirmation email", ComplianceResult.Deviates },
        { 3, "Rebooted the server and checked logs", "Servers must be rebooted weekly and logs reviewed after restart", ComplianceResult.Deviates },
        { 4, "Skipped torque confirmation at Station 3", "No guidelines exist for this case.", ComplianceResult.Unclear },
    };

    [Theory(Explicit = true)]
    [MemberData(nameof(BriefCases))]
    public async Task BriefCase_ClassifiesAsTheBriefExpects(int number, string action, string guideline, ComplianceResult expected)
    {
        // Same sources and key as the API: user-secrets, then the HuggingFace__ApiToken environment variable.
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets(ApiUserSecretsId)
            .AddEnvironmentVariables()
            .Build();
        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(configuration[$"{HuggingFaceOptions.SectionName}:{nameof(HuggingFaceOptions.ApiToken)}"]),
            "No HuggingFace:ApiToken configured (user-secrets or HuggingFace__ApiToken).");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddComplianceClassification(configuration);
        await using var provider = services.BuildServiceProvider();

        var outcome = await provider.GetRequiredService<IComplianceClassifier>()
            .ClassifyAsync(action, guideline, TestContext.Current.CancellationToken);

        Assert.True(
            outcome.Result == expected,
            $"Case {number}: expected {expected}, got {outcome.Result} " +
            $"(decided by {outcome.DecidedBy}, confidence {outcome.Confidence:0.000}, strategy {outcome.Strategy}).");
    }
}
