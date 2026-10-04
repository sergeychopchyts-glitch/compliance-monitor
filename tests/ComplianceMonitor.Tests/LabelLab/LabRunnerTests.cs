using System.Net;
using System.Text;
using System.Text.Json;
using ComplianceMonitor.Application.Compliance.Models;
using ComplianceMonitor.LabelLab;
using ComplianceMonitor.LabelLab.Strategies;
using ComplianceMonitor.Tests.TestSupport;
using Microsoft.Extensions.Options;

namespace ComplianceMonitor.Tests.LabelLab;

public sealed class LabRunnerTests
{
    private const string Complies = PlaceholderLabelStrategy.CompliesLabel;
    private const string Deviates = PlaceholderLabelStrategy.DeviatesLabel;

    private static readonly LabCase CompliesCase = new("Sent the email", "Emails must be sent", ComplianceResult.Complies, Brief: true);
    private static readonly LabCase NoGuidelineCase = new("Skipped torque check", "No guidelines exist for this case.", ComplianceResult.Unclear, Brief: true);

    private static LabRunner CreateRunner(HttpMessageHandler handler) =>
        new(TestClients.HuggingFace(handler), "facebook/bart-large-mnli", 0.5);

    private static HttpResponseMessage Scores(double complies, double deviates) => new(HttpStatusCode.OK)
    {
        // Lowest first, so position never decides.
        Content = new StringContent(
            JsonSerializer.Serialize(new[] { new { label = Complies, score = complies }, new { label = Deviates, score = deviates } }
                .OrderBy(s => s.score)),
            Encoding.UTF8,
            "application/json"),
    };

    private static Task<IReadOnlyList<StrategyReport>> Run(HttpMessageHandler handler, params LabCase[] cases) =>
        CreateRunner(handler).RunAsync([new PlaceholderLabelStrategy()], cases, TestContext.Current.CancellationToken);

    [Fact]
    public async Task RunAsync_ModelCase_ReportsPredictionTopScoreAndMargin()
    {
        var reports = await Run(new FakeHttpMessageHandler(_ => Scores(0.8, 0.2)), CompliesCase);

        var result = Assert.Single(Assert.Single(reports).Results);
        Assert.Equal(ComplianceResult.Complies, result.Predicted);
        Assert.Equal(DecisionReason.ModelClassification, result.Reason);
        Assert.Equal(0.8, result.TopScore);
        Assert.Equal(0.6, result.Margin!.Value, precision: 10);
        Assert.True(result.Passed);
    }

    [Fact]
    public async Task RunAsync_NoGuidelineCase_PredictsRuleButStillReportsModel()
    {
        var handler = new FakeHttpMessageHandler(_ => Scores(0.9, 0.1));

        var reports = await Run(handler, NoGuidelineCase);

        var result = Assert.Single(Assert.Single(reports).Results);
        Assert.Single(handler.Requests);
        Assert.Equal(ComplianceResult.Unclear, result.Predicted);
        Assert.Equal(DecisionReason.NoApplicableGuideline, result.Reason);
        Assert.Equal(ComplianceResult.Complies, result.ModelResult);
        Assert.True(result.Passed);
    }

    [Fact]
    public async Task RunAsync_Http402_StopsTheRun()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.PaymentRequired);

        var ex = await Assert.ThrowsAsync<CreditsExhaustedException>(() => Run(handler, CompliesCase, CompliesCase));

        Assert.Single(handler.Requests);
        Assert.Contains("402", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_OtherFailure_RecordsErrorAndContinues()
    {
        var calls = 0;
        var handler = new FakeHttpMessageHandler(_ => ++calls == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : Scores(0.8, 0.2));

        var reports = await Run(handler, CompliesCase, CompliesCase);

        var results = Assert.Single(reports).Results;
        Assert.Equal("Hugging Face returned HTTP 503.", results[0].Error);
        Assert.False(results[0].Passed);
        Assert.True(results[1].Passed);
    }
}
