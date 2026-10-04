namespace ComplianceMonitor.Application.Compliance.Models;

/// <param name="Confidence">
/// The model's score for <paramref name="Result"/>, only when the model chose that result. Null when a policy
/// decided: there is no honest "confidence in UNCLEAR" to report, and the model's own scores are kept separately.
/// </param>
public sealed record ComplianceDecision(ComplianceResult Result, double? Confidence, DecisionSource Source, DecisionReason Reason);
