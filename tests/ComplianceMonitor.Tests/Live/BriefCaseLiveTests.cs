using ComplianceMonitor.Application.Abstractions;
using ComplianceMonitor.Application.Compliance;
using ComplianceMonitor.Application.Compliance.Models;
using ComplianceMonitor.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ComplianceMonitor.Tests.Live;

/// <summary>
/// The four cases from docs/exercise.md through the real use case: the application's policies, the production
/// prompt and the real Hugging Face API. Explicit: plain `dotnet test` never runs them, even on a machine with a
/// token, so it makes no network calls. When requested (see CLAUDE.md) they skip unless a token is configured.
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
            string.IsNullOrWhiteSpace(configuration["HuggingFace:ApiToken"]),
            "No HuggingFace:ApiToken configured (user-secrets or HuggingFace__ApiToken).");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddInfrastructure(configuration);
        await using var provider = services.BuildServiceProvider();
        var service = new ComplianceAnalysisService(
            provider.GetRequiredService<IComplianceModelGateway>(),
            new DiscardingRepository(),
            TimeProvider.System,
            new ComplianceSettings(),
            NullLogger<ComplianceAnalysisService>.Instance);

        var result = await service.AnalyzeAsync(action, guideline, TestContext.Current.CancellationToken);

        Assert.True(
            result.Result == expected,
            $"Case {number}: expected {expected}, got {result.Result} " +
            $"(decided by {result.DecisionSource}/{result.DecisionReason}, confidence {result.Confidence?.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) ?? "none"}).");
    }

    /// <summary>The live tests check classification only; nothing is stored.</summary>
    private sealed class DiscardingRepository : IAnalysisRepository
    {
        public Task<AnalysisResult> AddAsync(NewAnalysis analysis, CancellationToken cancellationToken) =>
            Task.FromResult(new AnalysisResult(0, analysis.Action, analysis.Guideline, analysis.Decision.Result,
                analysis.Decision.Confidence, analysis.Decision.Source, analysis.Decision.Reason, analysis.CreatedAt));

        public Task<IReadOnlyList<AnalysisResult>> GetHistoryAsync(int limit, int offset, ComplianceResult? result, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AnalysisResult>>([]);

        public Task<AnalysisSummary> GetSummaryAsync(CancellationToken cancellationToken) => Task.FromResult(new AnalysisSummary(0, 0, 0, 0));
    }
}
