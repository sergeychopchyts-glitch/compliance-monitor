namespace ComplianceMonitor.Application.Compliance;

/// <summary>Fixed rules of the application, not configuration.</summary>
public static class ComplianceConstraints
{
    /// <summary>Maximum length of the action and of the guideline.</summary>
    public const int MaxTextLength = 2000;
}
