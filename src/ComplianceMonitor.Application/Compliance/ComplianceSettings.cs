namespace ComplianceMonitor.Application.Compliance;

/// <param name="ConfidenceThreshold">A model top score below this gives UNCLEAR (<see cref="Models.DecisionReason.InsufficientModelConfidence"/>).</param>
public sealed record ComplianceSettings(double ConfidenceThreshold)
{
    public const string SectionName = "Compliance";

    /// <summary>Maximum length of the action and of the guideline.</summary>
    public const int MaxTextLength = 2000;
}
