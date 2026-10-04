using ComplianceMonitor.Application.Compliance.Models;
using ComplianceMonitor.Application.Compliance.Rules;

namespace ComplianceMonitor.Application.Compliance;

/// <summary>
/// The application's decision rules around the model, in order. Shared by the API and tools/LabelLab so both
/// decide identically.
/// </summary>
public static class CompliancePolicy
{
    /// <summary>A decision that needs no model call, or null when the model must be asked.</summary>
    public static ComplianceDecision? DecideWithoutModel(string guideline) =>
        NoGuidelineRule.Matches(guideline)
            ? new ComplianceDecision(ComplianceResult.Unclear, null, DecisionSource.Rule, DecisionReason.NoApplicableGuideline)
            : null;

    public static ComplianceDecision Decide(string action, string guideline, ModelEvaluation model, double confidenceThreshold)
    {
        ArgumentNullException.ThrowIfNull(model);
        var top = model.Top;

        if (top.Score < confidenceThreshold)
        {
            return new ComplianceDecision(ComplianceResult.Unclear, null, DecisionSource.Rule, DecisionReason.InsufficientModelConfidence);
        }

        // Only a COMPLIES is overridden: an unrelated action stays UNCLEAR, and a DEVIATES is already a deviation.
        if (top.Result == ComplianceResult.Complies && TemporalRequirementRule.IsEvidenceMissing(action, guideline))
        {
            return new ComplianceDecision(ComplianceResult.Deviates, null, DecisionSource.Rule, DecisionReason.MissingTemporalEvidence);
        }

        return new ComplianceDecision(top.Result, top.Score, DecisionSource.Model, DecisionReason.ModelClassification);
    }
}
