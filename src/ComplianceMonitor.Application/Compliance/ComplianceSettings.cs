namespace ComplianceMonitor.Application.Compliance;

public sealed class ComplianceSettings
{
    public const string SectionName = "Compliance";

    /// <summary>Maximum length of the action and of the guideline.</summary>
    public const int MaxTextLength = 2000;

    /// <summary>A model top score below this gives UNCLEAR (<see cref="Models.DecisionReason.InsufficientModelConfidence"/>).</summary>
    public double ConfidenceThreshold { get; set; } = 0.5;
}
