using ComplianceMonitor.Application.Compliance.Models;
using ComplianceMonitor.Infrastructure.Integrations.HuggingFace.Strategies;

namespace ComplianceMonitor.LabelLab.Strategies;

/// <summary>
/// Strategy C: the comply/violate labels scored independently (multi_label). Measured with the same decision
/// policy as production, where UNCLEAR comes from the confidence threshold.
/// </summary>
public sealed class IndependentScoresPromptStrategy : IPromptStrategy
{
    public string Name => "independent-scores";

    public ZeroShotPrompt Build(string action, string guideline) =>
        new(
            Inputs: $"Action: {PromptStrategies.AsClause(action)}. Guideline: {PromptStrategies.AsClause(guideline)}",
            HypothesisTemplate: "This action {}.",
            MultiLabel: true,
            Labels: PromptStrategies.Labels(
                ("complies with the guideline", ComplianceResult.Complies),
                ("violates the guideline", ComplianceResult.Deviates)));
}
