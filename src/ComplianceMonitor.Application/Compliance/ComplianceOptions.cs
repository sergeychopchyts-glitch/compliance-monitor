namespace ComplianceMonitor.Application.Compliance;

/// <summary>Runtime configuration for the compliance policies, bound from the "Compliance" section.</summary>
public sealed class ComplianceOptions
{
    public const string SectionName = "Compliance";

    /// <summary>A model top score below this gives UNCLEAR (<see cref="Models.DecisionReason.InsufficientModelConfidence"/>).</summary>
    public double ConfidenceThreshold { get; set; } = 0.5;
}
