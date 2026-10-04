using System.Net;
using System.Text.Json.Nodes;
using ComplianceMonitor.Api.Classification;
using ComplianceMonitor.Api.Classification.Strategies;
using ComplianceMonitor.Tests.Infrastructure;
using Microsoft.Extensions.Options;

namespace ComplianceMonitor.Tests.Classification;

/// <summary>
/// The strategies are pre-registered in docs/plan.md §3; these tests pin their exact wording so a change is
/// deliberate and visible, never an accidental tweak to fit the measured cases.
/// </summary>
public sealed class LabelStrategyTests
{
    private const string Action = "Rebooted the server and checked logs.";
    private const string Guideline = "Servers must be rebooted weekly and logs reviewed after restart.";

    [Fact]
    public void CombinedThreeLabel_BuildsOnePremiseAndAnUnrelatedLabel()
    {
        var prompt = new CombinedThreeLabelStrategy().Build(Action, Guideline);

        Assert.Equal("Action: Rebooted the server and checked logs. Guideline: Servers must be rebooted weekly and logs reviewed after restart", prompt.Inputs);
        Assert.Equal("This action {}.", prompt.HypothesisTemplate);
        Assert.False(prompt.MultiLabel);
        Assert.Equal(
            [
                new("complies with the guideline", ComplianceResult.Complies),
                new("violates the guideline", ComplianceResult.Deviates),
                new KeyValuePair<string, ComplianceResult>("is unrelated to the guideline", ComplianceResult.Unclear),
            ],
            prompt.Labels);
    }

    [Fact]
    public void GuidelineHypothesis_PutsTheActionInThePremiseAndTheGuidelineInEachLabel()
    {
        var prompt = new GuidelineHypothesisStrategy().Build(Action, Guideline);

        Assert.Equal("Rebooted the server and checked logs.", prompt.Inputs);
        Assert.Equal("This action {}.", prompt.HypothesisTemplate);
        Assert.False(prompt.MultiLabel);
        Assert.Equal(
            [
                new("fully satisfies the requirement: Servers must be rebooted weekly and logs reviewed after restart", ComplianceResult.Complies),
                new("fails the requirement: Servers must be rebooted weekly and logs reviewed after restart", ComplianceResult.Deviates),
                new KeyValuePair<string, ComplianceResult>("has nothing to do with the requirement: Servers must be rebooted weekly and logs reviewed after restart", ComplianceResult.Unclear),
            ],
            prompt.Labels);
    }

    [Fact]
    public void IndependentScores_ScoresTwoLabelsIndependently()
    {
        var prompt = new IndependentScoresStrategy().Build(Action, Guideline);

        Assert.True(prompt.MultiLabel);
        Assert.Equal(["complies with the guideline", "violates the guideline"], prompt.Labels.Keys);
    }

    [Theory]
    [InlineData("Closed the ticket.", "Closed the ticket")]
    [InlineData("  Closed the ticket!  ", "Closed the ticket")]
    [InlineData("Closed the ticket", "Closed the ticket")]
    [InlineData("v1.2 shipped", "v1.2 shipped")]
    public void AsClause_TrimsOnlyTrailingPunctuation(string text, string expected) =>
        Assert.Equal(expected, LabelStrategies.AsClause(text));

    [Fact]
    public void All_HaveUniqueNamesAndAreFoundCaseInsensitively()
    {
        Assert.Equal(LabelStrategies.All.Count, LabelStrategies.All.Select(s => s.Name).Distinct().Count());
        Assert.IsType<GuidelineHypothesisStrategy>(LabelStrategies.Find("GUIDELINE-HYPOTHESIS"));
        Assert.Null(LabelStrategies.Find("nope"));
    }

    [Fact]
    public void Validate_UnknownStrategy_FailsListingTheValidNames()
    {
        var result = new HuggingFaceOptionsValidator().Validate(null, new HuggingFaceOptions { ApiToken = "t", Strategy = "nope" });

        Assert.True(result.Failed);
        Assert.Contains("HuggingFace:Strategy must be one of: placeholder, combined-three-label, guideline-hypothesis, independent-scores.", result.FailureMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0.9, 0.2, ComplianceResult.Complies, DecisionSource.Model)]
    [InlineData(0.1, 0.8, ComplianceResult.Deviates, DecisionSource.Model)]
    [InlineData(0.3, 0.4, ComplianceResult.Unclear, DecisionSource.LowConfidence)] // neither is entailed
    [InlineData(0.7, 0.6, ComplianceResult.Unclear, DecisionSource.Model)]         // both are: a contradiction
    public void Map_IndependentScores_UnclearWhenNeitherOrBothAreConfident(
        double complies, double violates, ComplianceResult expected, DecisionSource expectedSource)
    {
        var prompt = new IndependentScoresStrategy().Build(Action, Guideline);

        var mapped = LabelScoreMapper.Map(
            [new LabelScore("violates the guideline", violates), new LabelScore("complies with the guideline", complies)], prompt, 0.5);

        Assert.Equal(expected, mapped.Result);
        Assert.Equal(expectedSource, mapped.DecidedBy);
    }

    [Fact]
    public void Map_SingleLabelSoftmax_NeverAppliesTheBothConfidentRule()
    {
        // Softmax scores sum to 1, so two can't both be high; the rule only exists for multi_label.
        var prompt = new PlaceholderLabelStrategy().Build(Action, Guideline);

        var mapped = LabelScoreMapper.Map(
            [new LabelScore(PlaceholderLabelStrategy.CompliesLabel, 0.5), new LabelScore(PlaceholderLabelStrategy.DeviatesLabel, 0.5)], prompt, 0.5);

        Assert.Equal(DecisionSource.Model, mapped.DecidedBy);
        Assert.NotEqual(ComplianceResult.Unclear, mapped.Result);
    }

    [Fact]
    public async Task Api_SendsTheConfiguredStrategysPrompt()
    {
        var hf = FakeHttpMessageHandler.Returning(HttpStatusCode.OK, """
            [{"label":"fully satisfies the requirement: g","score":0.1},{"label":"fails the requirement: g","score":0.8},{"label":"has nothing to do with the requirement: g","score":0.1}]
            """);
        using var factory = new StrategyApiFactory("guideline-hypothesis") { HuggingFaceHandler = hf };

        using var response = await factory.CreateClient().PostAsync(
            new Uri("/analyze", UriKind.Relative),
            new StringContent("""{"action":"a","guideline":"g"}""", System.Text.Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("a.", (string?)JsonNode.Parse(Assert.Single(hf.Requests).Body!)!["inputs"]);
        var row = Assert.Single(await factory.GetAnalysesAsync());
        Assert.Equal("guideline-hypothesis", row.Strategy);
        Assert.Equal(ComplianceResult.Deviates, row.Result);
    }

    private sealed class StrategyApiFactory(string strategy) : ApiFactory
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("HuggingFace:Strategy", strategy);
        }
    }
}
