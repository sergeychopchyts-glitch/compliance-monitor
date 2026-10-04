using ComplianceMonitor.Application.Compliance.Models;
using ComplianceMonitor.Infrastructure.Integrations.HuggingFace.Strategies;

namespace ComplianceMonitor.LabelLab.Strategies;

/// <summary>
/// Strategy B, textbook NLI: the action is the premise and the guideline is written into each hypothesis.
/// "Fully" is meant to push partial compliance (brief Case 3) towards DEVIATES.
/// </summary>
public sealed class GuidelineHypothesisStrategy : ILabelStrategy
{
    public string Name => "guideline-hypothesis";

    public LabelPrompt Build(string action, string guideline)
    {
        var requirement = LabelStrategies.AsClause(guideline);
        return new(
            Inputs: LabelStrategies.AsClause(action) + ".",
            HypothesisTemplate: "This action {}.",
            MultiLabel: false,
            Labels: LabelStrategies.Labels(
                ($"fully satisfies the requirement: {requirement}", ComplianceResult.Complies),
                ($"fails the requirement: {requirement}", ComplianceResult.Deviates),
                ($"has nothing to do with the requirement: {requirement}", ComplianceResult.Unclear)));
    }
}
