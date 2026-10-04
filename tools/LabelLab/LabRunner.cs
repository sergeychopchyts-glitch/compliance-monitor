using ComplianceMonitor.Api.Classification;

namespace ComplianceMonitor.LabelLab;

/// <param name="Predicted">What the API would return: the no-guideline rule first, then the model.</param>
/// <param name="ModelResult">What the model alone decided, even when the rule fired; null on error.</param>
public sealed record CaseResult(
    int Number,
    LabCase Case,
    ComplianceResult? Predicted,
    DecisionSource? DecidedBy,
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

public sealed class LabRunner(HuggingFaceZeroShotClient client, double confidenceFloor)
{
    private readonly HuggingFaceZeroShotClient _client = client;
    private readonly double _confidenceFloor = confidenceFloor;

    public async Task<IReadOnlyList<StrategyReport>> RunAsync(
        IEnumerable<ILabelStrategy> strategies, IReadOnlyList<LabCase> cases, CancellationToken cancellationToken)
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
        ILabelStrategy strategy, int number, LabCase labCase, CancellationToken cancellationToken)
    {
        var prompt = strategy.Build(labCase.Action, labCase.Guideline);
        try
        {
            var scores = await _client.ClassifyAsync(prompt.ToRequest(), cancellationToken);
            var model = LabelScoreMapper.Map(scores, prompt, _confidenceFloor);
            var ranked = scores.Select(s => s.Score).OrderDescending().ToArray();
            var margin = ranked.Length > 1 ? ranked[0] - ranked[1] : ranked[0];

            var ruleFired = NoGuidelineRule.Matches(labCase.Guideline);
            return new CaseResult(
                number,
                labCase,
                Predicted: ruleFired ? ComplianceResult.Unclear : model.Result,
                DecidedBy: ruleFired ? DecisionSource.Rule : model.DecidedBy,
                ModelResult: model.Result,
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
