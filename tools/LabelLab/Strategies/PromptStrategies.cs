using ComplianceMonitor.Application.Compliance.Models;
using ComplianceMonitor.Infrastructure.Integrations.HuggingFace.Strategies;

namespace ComplianceMonitor.LabelLab.Strategies;

/// <summary>
/// Every strategy LabelLab measures: the production one, and the alternatives from docs/plan.md §3 that were
/// measured and not chosen. Only the production strategy ships in the API.
/// </summary>
public static class PromptStrategies
{
    public static IReadOnlyList<IPromptStrategy> All { get; } =
    [
        new ComplianceZeroShotPromptStrategy(),
        new PlaceholderPromptStrategy(),
        new GuidelineHypothesisPromptStrategy(),
        new IndependentScoresPromptStrategy(),
    ];

    public static IPromptStrategy? Find(string? name) =>
        All.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>"Rebooted the server." → "Rebooted the server", so sentences can be joined without "..".</summary>
    public static string AsClause(string text) => text.Trim().TrimEnd('.', '!', ';', ':', ' ');

    public static OrderedDictionary<string, ComplianceResult> Labels(params (string Text, ComplianceResult Result)[] labels)
    {
        var map = new OrderedDictionary<string, ComplianceResult>(StringComparer.Ordinal);
        foreach (var (text, result) in labels)
        {
            map.Add(text, result);
        }

        return map;
    }
}
