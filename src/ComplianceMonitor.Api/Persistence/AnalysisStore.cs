using ComplianceMonitor.Api.Classification;
using Microsoft.EntityFrameworkCore;

namespace ComplianceMonitor.Api.Persistence;

/// <param name="ByResult">Always has all three results; 0 when none are stored.</param>
public sealed record AnalysisSummary(int Total, IReadOnlyDictionary<ComplianceResult, int> ByResult);

/// <summary>All reads and writes of stored analyses. Deliberately specific, not a generic repository.</summary>
public sealed class AnalysisStore(ComplianceDbContext db)
{
    private readonly ComplianceDbContext _db = db;

    public async Task<AnalysisRecord> AddAsync(AnalysisRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        _db.Analyses.Add(record);
        await _db.SaveChangesAsync(cancellationToken);
        return record;
    }

    /// <summary>Newest first; <see cref="AnalysisRecord.Id"/> breaks ties between equal timestamps.</summary>
    public async Task<IReadOnlyList<AnalysisRecord>> GetHistoryAsync(
        int limit, int offset, ComplianceResult? result, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        var query = _db.Analyses.AsNoTracking();
        if (result is { } filter)
        {
            query = query.Where(a => a.Result == filter);
        }

        return await query
            .OrderByDescending(a => a.CreatedAt)
            .ThenByDescending(a => a.Id)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    /// <summary>One GROUP BY in SQL; results with no rows are filled in as 0.</summary>
    public async Task<AnalysisSummary> GetSummaryAsync(CancellationToken cancellationToken)
    {
        var counts = await _db.Analyses
            .GroupBy(a => a.Result)
            .Select(g => new { Result = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Result, g => g.Count, cancellationToken);

        var byResult = Enum.GetValues<ComplianceResult>().ToDictionary(r => r, r => counts.GetValueOrDefault(r));
        return new AnalysisSummary(byResult.Values.Sum(), byResult);
    }
}
