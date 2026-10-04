using ComplianceMonitor.Application.Compliance.Models;

namespace ComplianceMonitor.Infrastructure.Integrations.HuggingFace.Strategies;

/// <summary>
/// The production prompt (docs/plan.md §3, strategy A): action and guideline in one premise, and one candidate
/// label for each of COMPLIES, DEVIATES and UNCLEAR, as the brief suggests. Changing the wording is a new
/// <see cref="Name"/>: stored analyses record which prompt produced them.
/// </summary>
public sealed class ComplianceZeroShotPromptStrategy : IPromptStrategy
{
    public string Name => "combined-three-label-v1";

    public ZeroShotPrompt Build(string action, string guideline) =>
        new(
            Inputs: $"Action: {AsClause(action)}. Guideline: {AsClause(guideline)}",
            HypothesisTemplate: "This action {}.",
            MultiLabel: false,
            Labels: new OrderedDictionary<string, ComplianceResult>(StringComparer.Ordinal)
            {
                ["complies with the guideline"] = ComplianceResult.Complies,
                ["violates the guideline"] = ComplianceResult.Deviates,
                ["is unrelated to the guideline"] = ComplianceResult.Unclear,
            });

    /// <summary>"Rebooted the server." becomes "Rebooted the server", so sentences join without "..".</summary>
    public static string AsClause(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Trim().TrimEnd('.', '!', ';', ':', ' ');
    }
}
