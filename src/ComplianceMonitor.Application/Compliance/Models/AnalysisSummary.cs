namespace ComplianceMonitor.Application.Compliance.Models;

public sealed record AnalysisSummary(int Total, int Complies, int Deviates, int Unclear);
