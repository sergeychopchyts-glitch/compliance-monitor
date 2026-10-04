using ComplianceMonitor.Application.Compliance;
using ComplianceMonitor.Application.Compliance.Models;
using ComplianceMonitor.Infrastructure.Integrations.HuggingFace;
using ComplianceMonitor.Infrastructure.Integrations.HuggingFace.Strategies;

namespace ComplianceMonitor.LabelLab;

/// <param name="Predicted">What the API would return: the application's policies applied to the model's answer.</param>
/// <param name="Reason">Why: the model's classification, or which policy decided.</param>
/// <param name="ModelResult">The model's own top result, even when a policy decided; null on error.</param>
public sealed record CaseResult(
    int Number,
    LabCase Case,
    ComplianceResult? Predicted,
    DecisionReason? Reason,
    ComplianceResult? ModelResult,
    double? TopScore,
    double? Margin,
    string? Error)
{
    public bool Passed => Predicted == Case.Expected;
}

public sealed record StrategyReport(string Strategy, IReadOnlyList<CaseResult> Results);

/// <summary>Thrown when Hugging Face answers 402: further calls would also fail, so the run stops.</summary>
public sealed class CreditsExhaustedException(Exception innerException)
    : Exception("Hugging Face returned HTTP 402: inference credits are exhausted. Stopping; cached results are kept.", innerException);

public sealed class LabRunner(HuggingFaceZeroShotClient client, string modelId, double confidenceThreshold)
{
    private readonly HuggingFaceZeroShotClient _client = client;
    private readonly string _modelId = modelId;
    private readonly double _confidenceThreshold = confidenceThreshold;

    public async Task<IReadOnlyList<StrategyReport>> RunAsync(
        IEnumerable<IPromptStrategy> strategies, IReadOnlyList<LabCase> cases, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(strategies);
        ArgumentNullException.ThrowIfNull(cases);

        var reports = new List<StrategyReport>();
        foreach (var strategy in strategies)
        {
            var results = new List<CaseResult>();
            for (var i = 0; i < cases.Count; i++)
            {
                results.Add(await RunCaseAsync(strategy, i + 1, cases[i], cancellationToken));
            }

            reports.Add(new StrategyReport(strategy.Name, results));
        }

        return reports;
    }

    private async Task<CaseResult> RunCaseAsync(
        IPromptStrategy strategy, int number, LabCase labCase, CancellationToken cancellationToken)
    {
        var prompt = strategy.Build(labCase.Action, labCase.Guideline);
        try
        {
            var scores = await _client.ClassifyAsync(prompt.ToRequest(), cancellationToken);
            var evaluation = ZeroShotScoreMapper.ToEvaluation(scores, prompt, _modelId, strategy.Name);
            var ranked = scores.Select(s => s.Score).OrderDescending().ToArray();
            var margin = ranked.Length > 1 ? ranked[0] - ranked[1] : ranked[0];

            // The model is always asked, so its own answer is reported even when a policy decides.
            var decision = CompliancePolicy.DecideWithoutModel(labCase.Guideline)
                ?? CompliancePolicy.Decide(labCase.Action, labCase.Guideline, evaluation, _confidenceThreshold);
            return new CaseResult(
                number,
                labCase,
                Predicted: decision.Result,
                Reason: decision.Reason,
                ModelResult: evaluation.Top.Result,
                TopScore: ranked[0],
                Margin: margin,
                Error: null);
        }
        catch (HuggingFacePermanentException ex) when (ex.UpstreamStatusCode == 402)
        {
            throw new CreditsExhaustedException(ex);
        }
        catch (HuggingFaceException ex)
        {
            return new CaseResult(number, labCase, null, null, null, null, null, ex.Message);
        }
    }
}
