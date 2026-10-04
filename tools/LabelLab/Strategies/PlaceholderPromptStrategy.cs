using ComplianceMonitor.Application.Compliance.Models;
using ComplianceMonitor.Infrastructure.Integrations.HuggingFace.Strategies;

namespace ComplianceMonitor.LabelLab.Strategies;

/// <summary>The first strategy, kept as a baseline: the two labels from docs/hf-sample-response.json.</summary>
public sealed class PlaceholderPromptStrategy : IPromptStrategy
{
    public const string CompliesLabel = "complies with the guideline";
    public const string DeviatesLabel = "violates the guideline";

    public string Name => "placeholder";

    public ZeroShotPrompt Build(string action, string guideline) =>
        new(
            Inputs: $"Action: {action}\nGuideline: {guideline}",
            HypothesisTemplate: "This action {}.",
            MultiLabel: false,
            Labels: PromptStrategies.Labels(
                (CompliesLabel, ComplianceResult.Complies),
                (DeviatesLabel, ComplianceResult.Deviates)));
}
