using ComplianceMonitor.Application.Compliance.Models;

namespace ComplianceMonitor.Infrastructure.Integrations.HuggingFace.Strategies;

/// <summary>
/// Turns an (action, guideline) pair into a zero-shot prompt, and says what each candidate label means.
/// </summary>
public interface IPromptStrategy
{
    /// <summary>Stored with each analysis so results can be traced to the prompt that produced them.</summary>
    string Name { get; }

    ZeroShotPrompt Build(string action, string guideline);
}

/// <param name="Inputs">The premise sent as "inputs".</param>
/// <param name="HypothesisTemplate">Must contain "{}", which HF replaces with each label.</param>
/// <param name="MultiLabel">False: scores are a softmax across labels. True: each is an independent entailment score.</param>
/// <param name="Labels">
/// Candidate label text mapped to its result, compared ordinally. Enumeration order is the
/// candidate_labels order sent to HF, so use an ordered dictionary.
/// </param>
public sealed record ZeroShotPrompt(
    string Inputs,
    string HypothesisTemplate,
    bool MultiLabel,
    IReadOnlyDictionary<string, ComplianceResult> Labels)
{
    public ZeroShotRequest ToRequest() =>
        new(Inputs, new ZeroShotParameters([.. Labels.Keys], HypothesisTemplate, MultiLabel));
}
