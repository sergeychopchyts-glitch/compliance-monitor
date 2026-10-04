using ComplianceMonitor.Application.Abstractions;
using ComplianceMonitor.Application.Compliance.Models;
using Microsoft.Extensions.Logging;

namespace ComplianceMonitor.Application.Compliance;

public sealed partial class ComplianceAnalysisService(
    IComplianceModelGateway model,
    IAnalysisRepository repository,
    TimeProvider timeProvider,
    ComplianceOptions settings,
    ILogger<ComplianceAnalysisService> logger) : IComplianceAnalysisService
{
    private readonly IComplianceModelGateway _model = model;
    private readonly IAnalysisRepository _repository = repository;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ComplianceOptions _settings = settings;
    private readonly ILogger<ComplianceAnalysisService> _logger = logger;

    public async Task<AnalysisResult> AnalyzeAsync(string action, string guideline, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(guideline);
        action = action.Trim();
        guideline = guideline.Trim();

        var decision = CompliancePolicy.DecideWithoutModel(guideline);
        ModelEvaluation? evaluation = null;
        if (decision is null)
        {
            // A failure throws here, before anything is stored.
            evaluation = await _model.EvaluateAsync(action, guideline, cancellationToken);
            decision = CompliancePolicy.Decide(action, guideline, evaluation, _settings.ConfidenceThreshold);
        }

        var stored = await _repository.AddAsync(
            new NewAnalysis(
                action,
                guideline,
                decision,
                evaluation,
                evaluation is null ? null : _settings.ConfidenceThreshold,
                TruncateToSeconds(_timeProvider.GetUtcNow())),
            cancellationToken);

        // Never the action or guideline text: it is user input and may be sensitive.
        LogDecision(_logger, stored.Id, decision.Result, decision.Source, decision.Reason, evaluation?.Strategy);
        return stored;
    }

    public Task<IReadOnlyList<AnalysisResult>> GetHistoryAsync(
        int limit, int offset, ComplianceResult? result, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        return _repository.GetHistoryAsync(limit, offset, result, cancellationToken);
    }

    public Task<AnalysisSummary> GetSummaryAsync(CancellationToken cancellationToken) =>
        _repository.GetSummaryAsync(cancellationToken);

    private static DateTime TruncateToSeconds(DateTimeOffset now)
    {
        var utc = now.UtcDateTime;
        return new DateTime(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Analysis {Id}: {Result} by {Source} ({Reason}), strategy {Strategy}")]
    private static partial void LogDecision(
        ILogger logger, long id, ComplianceResult result, DecisionSource source, DecisionReason reason, string? strategy);
}
