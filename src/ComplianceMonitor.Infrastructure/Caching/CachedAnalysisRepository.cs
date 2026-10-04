using ComplianceMonitor.Application.Abstractions;
using ComplianceMonitor.Application.Compliance.Models;
using ComplianceMonitor.Infrastructure.Persistence.Repositories;
using Microsoft.Extensions.Caching.Hybrid;

namespace ComplianceMonitor.Infrastructure.Caching;

/// <summary>
/// Caches only the summary: it's a full-table GROUP BY that every dashboard refresh repeats. History is a cheap
/// indexed page and isn't cached; analyses (writes, with an audit trail) never are. Every successful insert
/// removes the cached summary, so this instance never serves a summary older than its own last write.
/// HybridCache keeps it in memory today; adding a distributed L2 (e.g. Redis) later needs no change here.
/// </summary>
internal sealed class CachedAnalysisRepository(AnalysisRepository inner, HybridCache cache) : IAnalysisRepository
{
    public static readonly TimeSpan SummaryLifetime = TimeSpan.FromSeconds(30);

    private static readonly HybridCacheEntryOptions SummaryOptions = new()
    {
        Expiration = SummaryLifetime,
        LocalCacheExpiration = SummaryLifetime,
    };

    private readonly AnalysisRepository _inner = inner;
    private readonly HybridCache _cache = cache;

    public async Task<AnalysisResult> AddAsync(NewAnalysis analysis, CancellationToken cancellationToken)
    {
        var added = await _inner.AddAsync(analysis, cancellationToken);
        await _cache.RemoveAsync(CacheKeys.AnalysisSummary, cancellationToken);
        return added;
    }

    public Task<IReadOnlyList<AnalysisResult>> GetHistoryAsync(int limit, int offset, ComplianceResult? result, CancellationToken cancellationToken) =>
        _inner.GetHistoryAsync(limit, offset, result, cancellationToken);

    public async Task<AnalysisSummary> GetSummaryAsync(CancellationToken cancellationToken) =>
        await _cache.GetOrCreateAsync(
            CacheKeys.AnalysisSummary,
            _inner,
            static (repository, ct) => new ValueTask<AnalysisSummary>(repository.GetSummaryAsync(ct)),
            SummaryOptions,
            cancellationToken: cancellationToken);
}
