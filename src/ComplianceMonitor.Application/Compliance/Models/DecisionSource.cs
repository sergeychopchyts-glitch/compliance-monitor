namespace ComplianceMonitor.Application.Compliance.Models;

/// <summary>Who made the final decision.</summary>
public enum DecisionSource
{
    /// <summary>The model's top result was accepted as-is.</summary>
    Model,

    /// <summary>An application policy decided, or overrode the model.</summary>
    Rule,
}
