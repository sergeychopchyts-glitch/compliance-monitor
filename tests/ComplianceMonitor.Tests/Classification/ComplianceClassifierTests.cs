using System.Net;
using System.Text.Json.Nodes;
using ComplianceMonitor.Api.Classification;
using ComplianceMonitor.Tests.Infrastructure;
using Microsoft.Extensions.Options;

namespace ComplianceMonitor.Tests.Classification;

public sealed class ComplianceClassifierTests
{
    private const string Complies = PlaceholderLabelStrategy.CompliesLabel;
    private const string Deviates = PlaceholderLabelStrategy.DeviatesLabel;
    private const string Action = "Closed ticket #48219 and sent confirmation email";
    private const string Guideline = "All closed tickets must include a confirmation email";

    private static ComplianceClassifier CreateClassifier(HttpMessageHandler handler, double confidenceFloor = 0.5)
    {
        var options = Options.Create(new HuggingFaceOptions { ApiToken = "token", ConfidenceFloor = confidenceFloor });
        return new ComplianceClassifier(
            TestClients.HuggingFace(handler, options.Value), new PlaceholderLabelStrategy(), options);
    }

    private static FakeHttpMessageHandler Responding(params (string Label, double Score)[] scores) =>
        FakeHttpMessageHandler.Returning(
            HttpStatusCode.OK,
            new JsonArray([.. scores.Select(s => new JsonObject { ["label"] = s.Label, ["score"] = s.Score })]).ToJsonString());

    private static Task<ClassificationOutcome> Classify(FakeHttpMessageHandler handler, string guideline = Guideline, double floor = 0.5) =>
        CreateClassifier(handler, floor).ClassifyAsync(Action, guideline, TestContext.Current.CancellationToken);

    [Theory]
    [InlineData("No guidelines exist for this case.")]
    [InlineData("no guideline exists")]
    [InlineData("NO   GUIDELINES\tEXIST here")]
    [InlineData("There is no applicable guideline.")]
    [InlineData("No guideline applies to Station 3")]
    public async Task ClassifyAsync_GuidelineSaysNoneExists_ReturnsUnclearByRuleWithoutHttpCall(string guideline)
    {
        var handler = Responding((Complies, 0.9), (Deviates, 0.1));

        var outcome = await Classify(handler, guideline);

        Assert.Empty(handler.Requests);
        Assert.Equal(ComplianceResult.Unclear, outcome.Result);
        Assert.Equal(DecisionSource.Rule, outcome.DecidedBy);
        Assert.Equal(1.0, outcome.Confidence);
        Assert.Empty(outcome.Scores);
    }

    [Theory]
    [InlineData("All closed tickets must include a confirmation email")]
    [InlineData("Guidelines exist for every station")]
    [InlineData("No ticket may be closed without a guideline review")]
    public void NoGuidelineRule_OrdinaryGuideline_DoesNotMatch(string guideline) =>
        Assert.False(NoGuidelineRule.Matches(guideline));

    [Fact]
    public async Task ClassifyAsync_SendsStrategyPromptToModel()
    {
        var handler = Responding((Complies, 0.9), (Deviates, 0.1));

        await Classify(handler);

        var body = JsonNode.Parse(Assert.Single(handler.Requests).Body!)!;
        Assert.Equal($"Action: {Action}\nGuideline: {Guideline}", (string?)body["inputs"]);
        Assert.True(JsonNode.DeepEquals(new JsonArray(Complies, Deviates), body["parameters"]!["candidate_labels"]));
        Assert.Equal("This action {}.", (string?)body["parameters"]!["hypothesis_template"]);
        Assert.False((bool)body["parameters"]!["multi_label"]!);
    }

    [Theory]
    [InlineData(0.8, 0.2, ComplianceResult.Complies, 0.8)]
    [InlineData(0.3, 0.7, ComplianceResult.Deviates, 0.7)]
    public async Task ClassifyAsync_MapsTopLabelByTextNotPosition(
        double compliesScore, double deviatesScore, ComplianceResult expected, double expectedConfidence)
    {
        // Lowest score first: the opposite of HF's sorted order, so index 0 is never the answer.
        var ordered = new[] { (Complies, compliesScore), (Deviates, deviatesScore) }.OrderBy(s => s.Item2).ToArray();

        var outcome = await Classify(Responding(ordered));

        Assert.Equal(expected, outcome.Result);
        Assert.Equal(expectedConfidence, outcome.Confidence);
        Assert.Equal(DecisionSource.Model, outcome.DecidedBy);
        Assert.Equal("placeholder", outcome.Strategy);
        Assert.Equal(2, outcome.Scores.Count);
    }

    [Theory]
    [InlineData(0.6, ComplianceResult.Complies, DecisionSource.Model)]
    [InlineData(0.5999, ComplianceResult.Unclear, DecisionSource.LowConfidence)]
    [InlineData(0.6001, ComplianceResult.Complies, DecisionSource.Model)]
    public async Task ClassifyAsync_ConfidenceFloor_AppliesBelowNotAt(
        double topScore, ComplianceResult expected, DecisionSource expectedSource)
    {
        var outcome = await Classify(Responding((Complies, topScore), (Deviates, 1 - topScore)), floor: 0.6);

        Assert.Equal(expected, outcome.Result);
        Assert.Equal(expectedSource, outcome.DecidedBy);
        Assert.Equal(topScore, outcome.Confidence);
    }

    public static TheoryData<string, double, string, double> BadLabelSets => new()
    {
        { Complies, 0.9, "unexpected label", 0.1 },
        { Complies, 0.9, Complies, 0.1 },
    };

    [Theory]
    [MemberData(nameof(BadLabelSets))]
    public async Task ClassifyAsync_LabelsDoNotMatchCandidates_ThrowsMalformed(
        string first, double firstScore, string second, double secondScore)
    {
        var ex = await Assert.ThrowsAsync<HuggingFacePermanentException>(
            () => Classify(Responding((first, firstScore), (second, secondScore))));

        Assert.True(ex.IsMalformedResponse);
    }

    [Fact]
    public async Task ClassifyAsync_CandidateLabelMissing_ThrowsMalformed()
    {
        var ex = await Assert.ThrowsAsync<HuggingFacePermanentException>(
            () => Classify(Responding((Complies, 1.0))));

        Assert.True(ex.IsMalformedResponse);
    }

    [Fact]
    public async Task ClassifyAsync_LabelDiffersOnlyInCase_ThrowsMalformed()
    {
        var ex = await Assert.ThrowsAsync<HuggingFacePermanentException>(
            () => Classify(Responding((Complies.ToUpperInvariant(), 0.9), (Deviates, 0.1))));

        Assert.True(ex.IsMalformedResponse);
    }
}
