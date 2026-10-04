namespace ComplianceMonitor.Application.Compliance.Models;

/// <param name="CreatedAt">UTC, whole seconds.</param>
public sealed record AnalysisResult(
    long Id,
    string Action,
    string Guideline,
    ComplianceResult Result,
    double? Confidence,
    DecisionSource DecisionSource,
    DecisionReason DecisionReason,
    DateTime CreatedAt);
