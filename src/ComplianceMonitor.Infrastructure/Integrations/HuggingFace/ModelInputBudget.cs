using System.Text;
using ComplianceMonitor.Infrastructure.Integrations.HuggingFace.Strategies;

namespace ComplianceMonitor.Infrastructure.Integrations.HuggingFace;

/// <param name="TokenUpperBound">A guaranteed upper bound on the tokens the longest premise/hypothesis pair needs.</param>
internal sealed record ModelInputCheck(bool Fits, int TokenUpperBound, int MaxTokens);

/// <summary>
/// bart-large-mnli reads at most 1024 tokens per premise/hypothesis pair, and Hugging Face silently truncates
/// anything longer: the end of the premise is cut and the answer can flip without any error (measured: a
/// DEVIATES case became COMPLIES once its decisive sentence was pushed past the limit).
/// The model uses byte-level BPE, where every token covers at least one UTF-8 byte, so the byte count is a
/// hard upper bound on the token count. Conservative, but it needs no tokenizer and can never under-count.
/// </summary>
internal static class ModelInputBudget
{
    public const int MaxTokens = 1024;

    /// <summary>BART encodes a pair as &lt;s&gt; premise &lt;/s&gt;&lt;/s&gt; hypothesis &lt;/s&gt;.</summary>
    public const int SpecialTokens = 4;

    public static ModelInputCheck Check(ZeroShotPrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        var longestHypothesis = prompt.Labels.Keys
            .Max(label => Encoding.UTF8.GetByteCount(prompt.HypothesisTemplate.Replace("{}", label, StringComparison.Ordinal)));
        var bound = Encoding.UTF8.GetByteCount(prompt.Inputs) + longestHypothesis + SpecialTokens;
        return new ModelInputCheck(bound <= MaxTokens, bound, MaxTokens);
    }
}
