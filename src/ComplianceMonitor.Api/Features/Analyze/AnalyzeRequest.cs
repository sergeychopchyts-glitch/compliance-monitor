using System.ComponentModel.DataAnnotations;
using ComplianceMonitor.Application.Compliance;

namespace ComplianceMonitor.Api.Features.Analyze;

/// <summary>
/// Validated by .NET 10 Minimal API validation (AddValidation). [Required] also rejects whitespace-only text.
/// Error keys are camel-cased in Program.cs to match the JSON contract.
/// </summary>
public sealed record AnalyzeRequest(
    [property: Required(ErrorMessage = "The action is required.")]
    [property: MaxLength(ComplianceConstraints.MaxTextLength, ErrorMessage = "The action must be at most 2000 characters.")]
    string Action,
    [property: Required(ErrorMessage = "The guideline is required.")]
    [property: MaxLength(ComplianceConstraints.MaxTextLength, ErrorMessage = "The guideline must be at most 2000 characters.")]
    string Guideline);
