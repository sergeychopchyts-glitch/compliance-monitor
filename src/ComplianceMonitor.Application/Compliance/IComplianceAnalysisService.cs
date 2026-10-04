using ComplianceMonitor.Application.Compliance.Models;

namespace ComplianceMonitor.Application.Compliance;

/// <summary>The compliance-analysis use case: classify, record, and report.</summary>
public interface IComplianceAnalysisService
{
    /// <exception cref="Errors.ModelGatewayException">The model could not be used; nothing is stored.</exception>
    Task<AnalysisResult> AnalyzeAsync(string action, string guideline, CancellationToken cancellationToken);

    Task<IReadOnlyList<AnalysisResult>> GetHistoryAsync(int limit, int offset, ComplianceResult? result, CancellationToken cancellationToken);

    Task<AnalysisSummary> GetSummaryAsync(CancellationToken cancellationToken);
}
