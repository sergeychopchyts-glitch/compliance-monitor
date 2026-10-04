namespace ComplianceMonitor.Api.Classification.Strategies;

/// <summary>
/// Strategy C: the comply/violate labels scored independently (multi_label). UNCLEAR comes from the scores:
/// neither label reaches the confidence floor, or both do (see <see cref="LabelScoreMapper"/>).
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
