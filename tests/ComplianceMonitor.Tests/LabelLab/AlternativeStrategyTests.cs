using ComplianceMonitor.Application.Compliance.Models;
using ComplianceMonitor.Infrastructure.Integrations.HuggingFace.Strategies;
using ComplianceMonitor.LabelLab.Strategies;

namespace ComplianceMonitor.Tests.LabelLab;

/// <summary>The measured-but-not-chosen strategies (docs/plan.md §3), pinned so LabelLab's numbers stay reproducible.</summary>
public sealed class AlternativeStrategyTests
{
    private const string Action = "Rebooted the server and checked logs.";
    private const string Guideline = "Servers must be rebooted weekly and logs reviewed after restart.";

    [Fact]
    public void All_StartsWithProductionAndHasUniqueNames()
    {
        Assert.IsType<ComplianceZeroShotStrategy>(LabelStrategies.All[0]);
        Assert.Equal(LabelStrategies.All.Count, LabelStrategies.All.Select(s => s.Name).Distinct().Count());
        Assert.IsType<GuidelineHypothesisStrategy>(LabelStrategies.Find("GUIDELINE-HYPOTHESIS"));
    }

    [Fact]
    public void Placeholder_TwoLabelsFromTheRecordedResponse()
    {
        var prompt = new PlaceholderLabelStrategy().Build(Action, Guideline);

        Assert.Equal($"Action: {Action}\nGuideline: {Guideline}", prompt.Inputs);
        Assert.Equal(["complies with the guideline", "violates the guideline"], prompt.Labels.Keys);
    }

    [Fact]
    public void GuidelineHypothesis_PutsTheGuidelineInEachLabel()
    {
        var prompt = new GuidelineHypothesisStrategy().Build(Action, Guideline);

        Assert.Equal("Rebooted the server and checked logs.", prompt.Inputs);
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
}
