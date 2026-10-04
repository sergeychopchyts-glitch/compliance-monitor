using ComplianceMonitor.Application.Compliance.Models;

namespace ComplianceMonitor.Application.Abstractions;

/// <summary>Storage for analyses, specific to this use case (not a generic repository).</summary>
public interface IAnalysisRepository
{
    Task<AnalysisResult> AddAsync(NewAnalysis analysis, CancellationToken cancellationToken);

    /// <summary>Newest first; ties broken by id.</summary>
    Task<IReadOnlyList<AnalysisResult>> GetHistoryAsync(int limit, int offset, ComplianceResult? result, CancellationToken cancellationToken);

    Task<AnalysisSummary> GetSummaryAsync(CancellationToken cancellationToken);
}
