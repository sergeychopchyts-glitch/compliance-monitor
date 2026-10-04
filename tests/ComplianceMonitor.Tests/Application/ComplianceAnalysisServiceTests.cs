using ComplianceMonitor.Application.Abstractions;
using ComplianceMonitor.Application.Compliance;
using ComplianceMonitor.Application.Compliance.Models;
using ComplianceMonitor.Application.Errors;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace ComplianceMonitor.Tests.Application;

public sealed class ComplianceAnalysisServiceTests
{
    private const string Action = "Closed ticket #48219 and sent confirmation email";
    private const string Guideline = "All closed tickets must include a confirmation email";

    private static readonly ModelEvaluation CompliesEvaluation = new(
        "HuggingFace", "facebook/bart-large-mnli", "three-label-v1",
        [
            new ModelScore("complies with the guideline", ComplianceResult.Complies, 0.9),
            new ModelScore("violates the guideline", ComplianceResult.Deviates, 0.07),
            new ModelScore("is unrelated to the guideline", ComplianceResult.Unclear, 0.03),
        ]);

    private readonly FakeModel _model = new();
    private readonly FakeRepository _repository = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 3, 10, 15, 0, 789, TimeSpan.Zero));

    private ComplianceAnalysisService CreateService() =>
        new(_model, _repository, _time, new ComplianceSettings { ConfidenceThreshold = 0.5 }, NullLogger<ComplianceAnalysisService>.Instance);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnalyzeAsync_ModelResult_IsAcceptedAndStoredWithFullAudit()
    {
        _model.Evaluation = CompliesEvaluation;

        var result = await CreateService().AnalyzeAsync($"  {Action} ", $"{Guideline}\t", Ct);

        Assert.Equal([(Action, Guideline)], _model.Calls);
        var stored = Assert.Single(_repository.Added);
        Assert.Equal(Action, stored.Action);
        Assert.Equal(Guideline, stored.Guideline);
        Assert.Equal(new ComplianceDecision(ComplianceResult.Complies, 0.9, DecisionSource.Model, DecisionReason.ModelClassification), stored.Decision);
        Assert.Same(CompliesEvaluation, stored.Model);
        Assert.Equal(0.5, stored.ConfidenceThreshold);
        Assert.Equal(new DateTime(2026, 10, 3, 10, 15, 0, DateTimeKind.Utc), stored.CreatedAt); // TimeProvider, whole seconds
        Assert.Equal(DateTimeKind.Utc, stored.CreatedAt.Kind);
        Assert.Equal(ComplianceResult.Complies, result.Result);
        Assert.Equal(1, result.Id);
    }

    [Fact]
    public async Task AnalyzeAsync_NoGuideline_DecidesByRuleWithoutCallingTheModel()
    {
        var result = await CreateService().AnalyzeAsync("Skipped torque confirmation at Station 3", "No guidelines exist for this case.", Ct);

        Assert.Empty(_model.Calls);
        var stored = Assert.Single(_repository.Added);
        Assert.Equal(DecisionReason.NoApplicableGuideline, stored.Decision.Reason);
        Assert.Null(stored.Model);
        Assert.Null(stored.ConfidenceThreshold);
        Assert.Null(result.Confidence);
        Assert.Equal(ComplianceResult.Unclear, result.Result);
    }

    [Fact]
    public async Task AnalyzeAsync_BriefCase3_TemporalPolicyOverridesTheModelAndKeepsItsScores()
    {
        _model.Evaluation = CompliesEvaluation;

        var result = await CreateService().AnalyzeAsync(
            "Rebooted the server and checked logs", "Servers must be rebooted weekly and logs reviewed after restart", Ct);

        Assert.Equal(ComplianceResult.Deviates, result.Result);
        Assert.Equal(DecisionSource.Rule, result.DecisionSource);
        Assert.Equal(DecisionReason.MissingTemporalEvidence, result.DecisionReason);
        Assert.Null(result.Confidence);
        Assert.Same(CompliesEvaluation, Assert.Single(_repository.Added).Model); // what the model said is still on record
    }

    [Fact]
    public async Task AnalyzeAsync_ModelFails_StoresNothingAndRethrows()
    {
        _model.Throws = new ModelGatewayException(ModelGatewayFailureKind.Unavailable, "down");

        var ex = await Assert.ThrowsAsync<ModelGatewayException>(() => CreateService().AnalyzeAsync(Action, Guideline, Ct));

        Assert.Equal(ModelGatewayFailureKind.Unavailable, ex.Kind);
        Assert.Empty(_repository.Added);
    }

    [Theory]
    [InlineData("", "g")]
    [InlineData("a", "   ")]
    public async Task AnalyzeAsync_BlankInput_IsRejected(string action, string guideline) =>
        await Assert.ThrowsAsync<ArgumentException>(() => CreateService().AnalyzeAsync(action, guideline, Ct));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, -1)]
    public async Task GetHistoryAsync_InvalidPaging_IsRejected(int limit, int offset) =>
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => CreateService().GetHistoryAsync(limit, offset, null, Ct));

    [Fact]
    public async Task GetHistoryAndSummary_PassThroughToTheRepository()
    {
        var service = CreateService();

        await service.GetHistoryAsync(5, 2, ComplianceResult.Deviates, Ct);
        var summary = await service.GetSummaryAsync(Ct);

        Assert.Equal((5, 2, ComplianceResult.Deviates), _repository.LastHistoryQuery);
        Assert.Same(_repository.Summary, summary);
    }

    private sealed class FakeModel : IComplianceModelGateway
    {
        public ModelEvaluation? Evaluation { get; set; }

        public Exception? Throws { get; set; }

        public List<(string Action, string Guideline)> Calls { get; } = [];

        public Task<ModelEvaluation> EvaluateAsync(string action, string guideline, CancellationToken cancellationToken)
        {
            Calls.Add((action, guideline));
            return Throws is null ? Task.FromResult(Evaluation!) : Task.FromException<ModelEvaluation>(Throws);
        }
    }

    private sealed class FakeRepository : IAnalysisRepository
    {
        public List<NewAnalysis> Added { get; } = [];

        public AnalysisSummary Summary { get; } = new(1, 1, 0, 0);

        public (int, int, ComplianceResult?)? LastHistoryQuery { get; private set; }

        public Task<AnalysisResult> AddAsync(NewAnalysis analysis, CancellationToken cancellationToken)
        {
            Added.Add(analysis);
            return Task.FromResult(new AnalysisResult(
                Added.Count, analysis.Action, analysis.Guideline, analysis.Decision.Result, analysis.Decision.Confidence,
                analysis.Decision.Source, analysis.Decision.Reason, analysis.CreatedAt));
        }

        public Task<IReadOnlyList<AnalysisResult>> GetHistoryAsync(int limit, int offset, ComplianceResult? result, CancellationToken cancellationToken)
        {
            LastHistoryQuery = (limit, offset, result);
            return Task.FromResult<IReadOnlyList<AnalysisResult>>([]);
        }

        public Task<AnalysisSummary> GetSummaryAsync(CancellationToken cancellationToken) => Task.FromResult(Summary);
    }
}
