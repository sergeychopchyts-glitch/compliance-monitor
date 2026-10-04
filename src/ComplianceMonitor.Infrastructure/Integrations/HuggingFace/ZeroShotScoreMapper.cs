using ComplianceMonitor.Application.Compliance.Models;
using ComplianceMonitor.Infrastructure.Integrations.HuggingFace.Strategies;

namespace ComplianceMonitor.Infrastructure.Integrations.HuggingFace;

/// <summary>
/// Turns HF zero-shot scores into a provider-neutral <see cref="ModelEvaluation"/>. Labels are matched by text,
/// never by position (HF sorts by score), and the response must cover exactly the labels that were sent.
/// Deciding what the scores mean is the Application's job (CompliancePolicy), not this class's.
/// </summary>
public static class ZeroShotScoreMapper
{
    public const string Provider = "HuggingFace";

    /// <exception cref="HuggingFacePermanentException">The labels returned are not exactly the labels sent.</exception>
    public static ModelEvaluation ToEvaluation(IReadOnlyList<LabelScore> scores, ZeroShotPrompt prompt, string modelId, string strategy)
    {
        ArgumentNullException.ThrowIfNull(scores);
        ArgumentNullException.ThrowIfNull(prompt);

        var returned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var score in scores)
        {
            if (!prompt.Labels.ContainsKey(score.Label))
            {
                throw HuggingFacePermanentException.Malformed("a returned label was not one we sent");
            }

            if (!returned.Add(score.Label))
            {
                throw HuggingFacePermanentException.Malformed("a label was returned twice");
            }
        }

        if (returned.Count != prompt.Labels.Count)
        {
            throw HuggingFacePermanentException.Malformed("a candidate label is missing from the response");
        }

        return new ModelEvaluation(
            Provider, modelId, strategy, [.. scores.Select(s => new ModelScore(s.Label, prompt.Labels[s.Label], s.Score))]);
    }
}
