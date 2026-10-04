using ComplianceMonitor.Application.Compliance.Models;
using ComplianceMonitor.Infrastructure.Integrations.HuggingFace.Strategies;

namespace ComplianceMonitor.LabelLab.Strategies;

/// <summary>
/// Strategy C: the comply/violate labels scored independently (multi_label). Measured with the same decision
/// policy as production, where UNCLEAR comes from the confidence threshold.
/// </summary>
public sealed class IndependentScoresStrategy : ILabelStrategy
{
    public string Name => "independent-scores";

    public LabelPrompt Build(string action, string guideline) =>
        new(
            Inputs: $"Action: {LabelStrategies.AsClause(action)}. Guideline: {LabelStrategies.AsClause(guideline)}",
            HypothesisTemplate: "This action {}.",
            MultiLabel: true,
            Labels: LabelStrategies.Labels(
                ("complies with the guideline", ComplianceResult.Complies),
                ("violates the guideline", ComplianceResult.Deviates)));
}
