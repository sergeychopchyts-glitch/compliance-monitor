using Microsoft.Extensions.Options;

namespace ComplianceMonitor.Api.Classification;

public interface IComplianceClassifier
{
    /// <summary>Whether this pair fits the model's input without truncation. Callers validate with it first.</summary>
    ModelInputCheck CheckInput(string action, string guideline);

    /// <exception cref="HuggingFaceException">The model could not be used. Nothing should be stored.</exception>
    /// <exception cref="ArgumentException">The pair does not fit the model's input (see <see cref="CheckInput"/>).</exception>
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

public sealed partial class ComplianceClassifier(
    HuggingFaceZeroShotClient client,
    ILabelStrategy strategy,
    IOptions<HuggingFaceOptions> options,
    ILogger<ComplianceClassifier> logger) : IComplianceClassifier
{
    private readonly HuggingFaceZeroShotClient _client = client;
    private readonly ILabelStrategy _strategy = strategy;
    private readonly double _confidenceFloor = options.Value.ConfidenceFloor;
    private readonly ILogger<ComplianceClassifier> _logger = logger;

    public ModelInputCheck CheckInput(string action, string guideline) =>
        NoGuidelineRule.Matches(guideline)
            ? new ModelInputCheck(true, 0, ModelInputBudget.MaxTokens) // decided by the rule; the model is never asked
            : ModelInputBudget.Check(_strategy.Build(action, guideline));

    public async Task<ClassificationOutcome> ClassifyAsync(
        string action, string guideline, CancellationToken cancellationToken)
    {
        if (NoGuidelineRule.Matches(guideline))
        {
            LogClassified(_logger, _strategy.Name, ComplianceResult.Unclear, DecisionSource.Rule, 1.0);
            return new ClassificationOutcome(
                ComplianceResult.Unclear, 1.0, DecisionSource.Rule, _strategy.Name, []);
        }

        var prompt = _strategy.Build(action, guideline);
        if (!ModelInputBudget.Check(prompt).Fits)
        {
            // Never send a prompt HF would silently truncate; the endpoint rejects these with a 400 first.
            throw new ArgumentException("The action and guideline do not fit the model's input.", nameof(action));
        }

        var scores = await _client.ClassifyAsync(prompt.ToRequest(), cancellationToken);
        var mapped = LabelScoreMapper.Map(scores, prompt, _confidenceFloor);

        LogClassified(_logger, _strategy.Name, mapped.Result, mapped.DecidedBy, mapped.Confidence);
        return new ClassificationOutcome(mapped.Result, mapped.Confidence, mapped.DecidedBy, _strategy.Name, scores);
    }

    // No action or guideline text: they are user input and may contain anything.
    [LoggerMessage(Level = LogLevel.Information,
        Message = "Classified with strategy {Strategy}: {Result} decided by {DecidedBy}, confidence {Confidence}")]
    private static partial void LogClassified(
        ILogger logger, string strategy, ComplianceResult result, DecisionSource decidedBy, double confidence);
}
