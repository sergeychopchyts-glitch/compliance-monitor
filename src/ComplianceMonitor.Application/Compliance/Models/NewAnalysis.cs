namespace ComplianceMonitor.Application.Compliance.Models;

/// <summary>Everything stored for one analysis, including the audit trail of how it was decided.</summary>
/// <param name="Model">Null when a policy decided without calling the model.</param>
/// <param name="ConfidenceThreshold">The threshold in force; null when the model was not called.</param>
public sealed record NewAnalysis(
    string Action,
    string Guideline,
    ComplianceDecision Decision,
    ModelEvaluation? Model,
    double? ConfidenceThreshold,
    DateTime CreatedAt);
