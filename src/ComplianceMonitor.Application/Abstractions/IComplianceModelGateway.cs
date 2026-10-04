using ComplianceMonitor.Application.Compliance.Models;

namespace ComplianceMonitor.Application.Abstractions;

/// <summary>Asks a language model how an action relates to a guideline. Provider-neutral.</summary>
public interface IComplianceModelGateway
{
    /// <exception cref="Errors.ModelGatewayException">The model could not be used; nothing should be stored.</exception>
    Task<ModelEvaluation> EvaluateAsync(string action, string guideline, CancellationToken cancellationToken);
}
