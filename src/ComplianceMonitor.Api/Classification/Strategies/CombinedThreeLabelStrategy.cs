namespace ComplianceMonitor.Api.Classification.Strategies;

/// <summary>Strategy A: action and guideline in one premise, three generic labels, one of them UNCLEAR.</summary>
public sealed class CombinedThreeLabelStrategy : ILabelStrategy
{
    public string Name => "combined-three-label";

    public LabelPrompt Build(string action, string guideline) =>
        new(
            Inputs: $"Action: {LabelStrategies.AsClause(action)}. Guideline: {LabelStrategies.AsClause(guideline)}",
            HypothesisTemplate: "This action {}.",
            MultiLabel: false,
            Labels: LabelStrategies.Labels(
                ("complies with the guideline", ComplianceResult.Complies),
                ("violates the guideline", ComplianceResult.Deviates),
                ("is unrelated to the guideline", ComplianceResult.Unclear)));
}
