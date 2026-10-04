using ComplianceMonitor.Application.Compliance.Models;

namespace ComplianceMonitor.Infrastructure.Persistence.Entities;

/// <summary>One stored analysis: the decision, and the audit trail of how it was reached.</summary>
internal sealed class AnalysisEntity
{
    public long Id { get; set; }

    public required string Action { get; set; }

    public required string Guideline { get; set; }

    public ComplianceResult Result { get; set; }

    /// <summary>The model's score for <see cref="Result"/>; null when a policy decided.</summary>
    public double? Confidence { get; set; }

    public DecisionSource DecisionSource { get; set; }

    public DecisionReason DecisionReason { get; set; }

    // What the model said, when it was asked. All null when a policy decided without it.
    public string? ModelProvider { get; set; }

    public string? ModelId { get; set; }

    public string? Strategy { get; set; }

    public ComplianceResult? ModelTopResult { get; set; }

    public double? ModelTopScore { get; set; }

    /// <summary>Every candidate label with its result and score, as JSON.</summary>
    public string? RawScoresJson { get; set; }

    public double? ConfidenceThreshold { get; set; }

    /// <summary>UTC, whole seconds. A DateTime, because EF's SQLite provider can't order or compare DateTimeOffset.</summary>
    public DateTime CreatedAt { get; set; }
}
