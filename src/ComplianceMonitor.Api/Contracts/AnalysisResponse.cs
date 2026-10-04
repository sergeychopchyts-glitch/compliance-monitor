using ComplianceMonitor.Application.Compliance.Models;

namespace ComplianceMonitor.Api.Contracts;

/// <summary>One analysis, as returned by POST /analyze and GET /history: shared, so it lives outside either feature.</summary>
/// <param name="Confidence">The model's score for <paramref name="Result"/>, rounded to 2 decimals; null when a policy decided.</param>
/// <param name="Timestamp">UTC, serialized with a trailing Z.</param>
public sealed record AnalysisResponse(
    long Id,
    string Action,
    string Guideline,
    ComplianceResult Result,
    double? Confidence,
    DecisionSource DecisionSource,
    DecisionReason DecisionReason,
    DateTime Timestamp)
{
    public static AnalysisResponse From(AnalysisResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new(
            result.Id,
            result.Action,
            result.Guideline,
            result.Result,
            result.Confidence is { } c ? Math.Round(c, 2, MidpointRounding.AwayFromZero) : null,
            result.DecisionSource,
            result.DecisionReason,
            result.CreatedAt);
    }
}
