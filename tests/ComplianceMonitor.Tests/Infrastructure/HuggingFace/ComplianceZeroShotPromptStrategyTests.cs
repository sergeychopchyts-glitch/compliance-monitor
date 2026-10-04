using ComplianceMonitor.Application.Compliance.Models;
using ComplianceMonitor.Infrastructure.Integrations.HuggingFace.Strategies;

namespace ComplianceMonitor.Tests.Infrastructure.HuggingFace;

/// <summary>Pins the production prompt: a wording change must be deliberate, and comes with a new strategy name.</summary>
public sealed class ComplianceZeroShotPromptStrategyTests
{
    [Fact]
    public void Build_OnePremiseAndALabelForEachResult()
    {
        var prompt = new ComplianceZeroShotPromptStrategy().Build(
            "Rebooted the server and checked logs.", "Servers must be rebooted weekly and logs reviewed after restart.");

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
        Assert.Equal("combined-three-label-v1", new ComplianceZeroShotPromptStrategy().Name);
    }

    [Theory]
    [InlineData("Closed the ticket.", "Closed the ticket")]
    [InlineData("  Closed the ticket!  ", "Closed the ticket")]
    [InlineData("Closed the ticket", "Closed the ticket")]
    [InlineData("v1.2 shipped", "v1.2 shipped")]
    public void AsClause_TrimsOnlyTrailingPunctuation(string text, string expected) =>
        Assert.Equal(expected, ComplianceZeroShotPromptStrategy.AsClause(text));
}
