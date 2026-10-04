namespace ComplianceMonitor.Api.Classification.Strategies;

/// <summary>Every strategy the API can run, selected by <c>HuggingFace:Strategy</c>; docs/plan.md §3 describes them.</summary>
public static class LabelStrategies
{
    public static IReadOnlyList<ILabelStrategy> All { get; } =
    [
        new PlaceholderLabelStrategy(),
        new CombinedThreeLabelStrategy(),
        new GuidelineHypothesisStrategy(),
        new IndependentScoresStrategy(),
    ];

    public static ILabelStrategy? Find(string? name) =>
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
