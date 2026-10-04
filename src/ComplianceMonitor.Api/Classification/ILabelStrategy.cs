namespace ComplianceMonitor.Api.Classification;

/// <summary>
/// Turns an (action, guideline) pair into a zero-shot prompt, and says what each candidate label means.
/// </summary>
public interface ILabelStrategy
{
    /// <summary>Stored with each analysis so results can be traced to the prompt that produced them.</summary>
    string Name { get; }

    LabelPrompt Build(string action, string guideline);
}

/// <param name="Inputs">The premise sent as "inputs".</param>
/// <param name="HypothesisTemplate">Must contain "{}", which HF replaces with each label.</param>
/// <param name="MultiLabel">False: scores are a softmax across labels. True: each is an independent entailment score.</param>
/// <param name="Labels">
/// Candidate label text mapped to its result, compared ordinally. Enumeration order is the
/// candidate_labels order sent to HF, so use an ordered dictionary.
/// </param>
public sealed record LabelPrompt(
    string Inputs,
    string HypothesisTemplate,
    bool MultiLabel,
    IReadOnlyDictionary<string, ComplianceResult> Labels);

/// <summary>
/// Stand-in until the real strategies (docs/plan.md, section 3) are written.
/// Uses the two labels from docs/hf-sample-response.json.
/// </summary>
public sealed class PlaceholderLabelStrategy : ILabelStrategy
{
    public const string CompliesLabel = "complies with the guideline";
    public const string DeviatesLabel = "violates the guideline";

    public string Name => "placeholder";

    public LabelPrompt Build(string action, string guideline) =>
        new(
            Inputs: $"Action: {action}\nGuideline: {guideline}",
            HypothesisTemplate: "This action {}.",
            MultiLabel: false,
            Labels: new OrderedDictionary<string, ComplianceResult>(StringComparer.Ordinal)
            {
                [CompliesLabel] = ComplianceResult.Complies,
                [DeviatesLabel] = ComplianceResult.Deviates,
            });
}
