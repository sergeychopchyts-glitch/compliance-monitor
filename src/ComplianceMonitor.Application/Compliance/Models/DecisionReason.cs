namespace ComplianceMonitor.Application.Compliance.Models;

/// <summary>Why the final decision is what it is.</summary>
public enum DecisionReason
{
    /// <summary>The model's top result, at or above the confidence threshold.</summary>
    ModelClassification,

    /// <summary>The guideline states that no guideline exists, so there is nothing to judge against.</summary>
    NoApplicableGuideline,

    /// <summary>The guideline requires a frequency the action shows no evidence of, so it can't fully comply.</summary>
    MissingTemporalEvidence,

    /// <summary>The model's top score was below the confidence threshold.</summary>
    InsufficientModelConfidence,
}
