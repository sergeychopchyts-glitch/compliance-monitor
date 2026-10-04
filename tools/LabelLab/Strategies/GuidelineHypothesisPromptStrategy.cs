using ComplianceMonitor.Application.Compliance.Models;
using ComplianceMonitor.Infrastructure.Integrations.HuggingFace.Strategies;

namespace ComplianceMonitor.LabelLab.Strategies;

/// <summary>
/// Strategy B, textbook NLI: the action is the premise and the guideline is written into each hypothesis.
/// "Fully" is meant to push partial compliance (brief Case 3) towards DEVIATES.
/// </summary>
public sealed class GuidelineHypothesisPromptStrategy : IPromptStrategy
{
    public string Name => "guideline-hypothesis";

    public ZeroShotPrompt Build(string action, string guideline)
    {
        var requirement = PromptStrategies.AsClause(guideline);
        return new(
            Inputs: PromptStrategies.AsClause(action) + ".",
            HypothesisTemplate: "This action {}.",
            MultiLabel: false,
            Labels: PromptStrategies.Labels(
                ($"fully satisfies the requirement: {requirement}", ComplianceResult.Complies),
                ($"fails the requirement: {requirement}", ComplianceResult.Deviates),
                ($"has nothing to do with the requirement: {requirement}", ComplianceResult.Unclear)));
    }
}
