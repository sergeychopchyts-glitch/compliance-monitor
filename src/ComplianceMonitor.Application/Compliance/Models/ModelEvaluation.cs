namespace ComplianceMonitor.Application.Compliance.Models;

/// <summary>One candidate label, the result it stands for, and its score.</summary>
public sealed record ModelScore(string Label, ComplianceResult Result, double Score);

/// <summary>What the model said, provider-neutral, with enough provenance to explain it later.</summary>
/// <param name="Strategy">Name and version of the prompt strategy that produced <paramref name="Scores"/>.</param>
public sealed record ModelEvaluation(string Provider, string ModelId, string Strategy, IReadOnlyList<ModelScore> Scores)
{
    public ModelScore Top => Scores.MaxBy(s => s.Score)
        ?? throw new InvalidOperationException("A model evaluation needs at least one score.");
}
