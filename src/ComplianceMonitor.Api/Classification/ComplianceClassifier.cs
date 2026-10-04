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
        var request = new ZeroShotRequest(
            prompt.Inputs,
            new ZeroShotParameters([.. prompt.Labels.Keys], prompt.HypothesisTemplate, prompt.MultiLabel));

        var scores = await _client.ClassifyAsync(request, cancellationToken);
        var top = PickTop(scores, prompt.Labels);

        return top.Score < _confidenceFloor
            ? new ClassificationOutcome(
                ComplianceResult.Unclear, top.Score, DecisionSource.LowConfidence, _strategy.Name, scores)
            : new ClassificationOutcome(
                prompt.Labels[top.Label], top.Score, DecisionSource.Model, _strategy.Name, scores);
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
