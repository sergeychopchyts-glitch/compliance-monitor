using Microsoft.Extensions.Options;

namespace ComplianceMonitor.Api.Classification;

public interface IComplianceClassifier
{
    /// <exception cref="HuggingFaceException">The model could not be used. Nothing should be stored.</exception>
    Task<ClassificationOutcome> ClassifyAsync(string action, string guideline, CancellationToken cancellationToken);
}

/// <param name="Confidence">Top label score; 1.0 when a rule decided.</param>
/// <param name="Strategy">Name of the label strategy in effect.</param>
/// <param name="Scores">Raw scores from HF, empty when a rule decided.</param>
public sealed record ClassificationOutcome(
    ComplianceResult Result,
    double Confidence,
    DecisionSource DecidedBy,
    string Strategy,
    IReadOnlyList<LabelScore> Scores);

public sealed class ComplianceClassifier(
    HuggingFaceZeroShotClient client,
    ILabelStrategy strategy,
    IOptions<HuggingFaceOptions> options) : IComplianceClassifier
{
    private readonly HuggingFaceZeroShotClient _client = client;
    private readonly ILabelStrategy _strategy = strategy;
    private readonly double _confidenceFloor = options.Value.ConfidenceFloor;

    public async Task<ClassificationOutcome> ClassifyAsync(
        string action, string guideline, CancellationToken cancellationToken)
    {
        if (NoGuidelineRule.Matches(guideline))
        {
            return new ClassificationOutcome(
                ComplianceResult.Unclear, 1.0, DecisionSource.Rule, _strategy.Name, []);
        }

        var prompt = _strategy.Build(action, guideline);
        var scores = await _client.ClassifyAsync(prompt.ToRequest(), cancellationToken);
        var mapped = LabelScoreMapper.Map(scores, prompt.Labels, _confidenceFloor);

        return new ClassificationOutcome(mapped.Result, mapped.Confidence, mapped.DecidedBy, _strategy.Name, scores);
    }
}

public sealed record MappedScore(ComplianceResult Result, double Confidence, DecisionSource DecidedBy);

/// <summary>
/// Turns zero-shot scores into a result. Shared by the API and tools/LabelLab so both decide identically.
/// </summary>
public static class LabelScoreMapper
{
    /// <exception cref="HuggingFacePermanentException">The labels returned are not exactly the labels sent.</exception>
    public static MappedScore Map(
        IReadOnlyList<LabelScore> scores, IReadOnlyDictionary<string, ComplianceResult> labels, double confidenceFloor)
    {
        ArgumentNullException.ThrowIfNull(scores);
        ArgumentNullException.ThrowIfNull(labels);

        var top = PickTop(scores, labels);
        return top.Score < confidenceFloor
            ? new MappedScore(ComplianceResult.Unclear, top.Score, DecisionSource.LowConfidence)
            : new MappedScore(labels[top.Label], top.Score, DecisionSource.Model);
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

/// <summary>
/// Precondition: when the guideline itself says there is no guideline, there is nothing to
/// judge against, so the result is UNCLEAR without asking the model. Deliberately a short,
/// explicit phrase list; outcomes are marked <see cref="DecisionSource.Rule"/>.
/// </summary>
public static class NoGuidelineRule
{
    private static readonly string[] Phrases =
    [
        "no guideline exists",
        "no guidelines exist",
        "no guideline applies",
        "no guidelines apply",
        "no applicable guideline",
    ];

    public static bool Matches(string guideline)
    {
        ArgumentNullException.ThrowIfNull(guideline);
        var normalized = string.Join(' ', guideline.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return Phrases.Any(p => normalized.Contains(p, StringComparison.OrdinalIgnoreCase));
    }
}
