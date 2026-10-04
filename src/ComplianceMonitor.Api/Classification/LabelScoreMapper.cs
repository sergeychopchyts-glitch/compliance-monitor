namespace ComplianceMonitor.Api.Classification;

public sealed record MappedScore(ComplianceResult Result, double Confidence, DecisionSource DecidedBy);

/// <summary>
/// Turns zero-shot scores into a result. Shared by the API and tools/LabelLab so both decide identically.
/// </summary>
public static class LabelScoreMapper
{
    /// <exception cref="HuggingFacePermanentException">The labels returned are not exactly the labels sent.</exception>
    public static MappedScore Map(IReadOnlyList<LabelScore> scores, LabelPrompt prompt, double confidenceFloor)
    {
        ArgumentNullException.ThrowIfNull(scores);
        ArgumentNullException.ThrowIfNull(prompt);
        var labels = prompt.Labels;

        var top = PickTop(scores, labels);
        if (top.Score < confidenceFloor)
        {
            return new MappedScore(ComplianceResult.Unclear, top.Score, DecisionSource.LowConfidence);
        }

        // With independent scores (multi_label) the model can entail both "complies" and "violates".
        // It contradicts itself, so the honest answer is UNCLEAR.
        var confidentVerdicts = scores
            .Where(s => s.Score >= confidenceFloor && labels[s.Label] != ComplianceResult.Unclear)
            .Select(s => labels[s.Label])
            .Distinct()
            .Count();
        if (prompt.MultiLabel && confidentVerdicts > 1)
        {
            return new MappedScore(ComplianceResult.Unclear, top.Score, DecisionSource.Model);
        }

        return new MappedScore(labels[top.Label], top.Score, DecisionSource.Model);
    }

    // HF sorts by score, but we never rely on position: every label is matched by text,
    // and the response must cover exactly the labels we sent.
    private static LabelScore PickTop(
        IReadOnlyList<LabelScore> scores, IReadOnlyDictionary<string, ComplianceResult> labels)
    {
        var returned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var score in scores)
        {
            if (!labels.ContainsKey(score.Label))
            {
                throw HuggingFacePermanentException.Malformed("a returned label was not one we sent");
            }

            if (!returned.Add(score.Label))
            {
                throw HuggingFacePermanentException.Malformed("a label was returned twice");
            }
        }

        if (returned.Count != labels.Count)
        {
            throw HuggingFacePermanentException.Malformed("a candidate label is missing from the response");
        }

        return scores.MaxBy(s => s.Score)!;
    }
}
