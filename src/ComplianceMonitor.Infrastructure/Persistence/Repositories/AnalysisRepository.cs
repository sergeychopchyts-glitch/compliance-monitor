using System.Text.Json;
using ComplianceMonitor.Application.Abstractions;
using ComplianceMonitor.Application.Compliance.Models;
using ComplianceMonitor.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace ComplianceMonitor.Infrastructure.Persistence.Repositories;

public sealed class AnalysisRepository(ComplianceDbContext db) : IAnalysisRepository
{
    private readonly ComplianceDbContext _db = db;

    public async Task<AnalysisResult> AddAsync(NewAnalysis analysis, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        var model = analysis.Model;
        var entity = new AnalysisEntity
        {
            Action = analysis.Action,
            Guideline = analysis.Guideline,
            Result = analysis.Decision.Result,
            Confidence = analysis.Decision.Confidence,
            DecisionSource = analysis.Decision.Source,
            DecisionReason = analysis.Decision.Reason,
            ModelProvider = model?.Provider,
            ModelId = model?.ModelId,
            Strategy = model?.Strategy,
            ModelTopResult = model?.Top.Result,
            ModelTopScore = model?.Top.Score,
            RawScoresJson = model is null ? null : JsonSerializer.Serialize(model.Scores, JsonSerializerOptions.Web),
            ConfidenceThreshold = analysis.ConfidenceThreshold,
            CreatedAt = analysis.CreatedAt,
        };
        _db.Analyses.Add(entity);
        await _db.SaveChangesAsync(cancellationToken);
        return ToResult(entity);
    }

    public async Task<IReadOnlyList<AnalysisResult>> GetHistoryAsync(
        int limit, int offset, ComplianceResult? result, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        var query = _db.Analyses.AsNoTracking();
        if (result is { } filter)
        {
            query = query.Where(a => a.Result == filter);
        }

        var entities = await query
            .OrderByDescending(a => a.CreatedAt)
            .ThenByDescending(a => a.Id)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(cancellationToken);
        return [.. entities.Select(ToResult)];
    }

    /// <summary>One GROUP BY in SQL; results with no rows count as 0.</summary>
    public async Task<AnalysisSummary> GetSummaryAsync(CancellationToken cancellationToken)
    {
        var counts = await _db.Analyses
            .GroupBy(a => a.Result)
            .Select(g => new { Result = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Result, g => g.Count, cancellationToken);

        return new AnalysisSummary(
            counts.Values.Sum(),
            counts.GetValueOrDefault(ComplianceResult.Complies),
            counts.GetValueOrDefault(ComplianceResult.Deviates),
            counts.GetValueOrDefault(ComplianceResult.Unclear));
    }

    private static AnalysisResult ToResult(AnalysisEntity e) =>
        new(e.Id, e.Action, e.Guideline, e.Result, e.Confidence, e.DecisionSource, e.DecisionReason, e.CreatedAt);
}
