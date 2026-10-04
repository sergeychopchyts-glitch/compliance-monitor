using System.Text.Json.Serialization;
using ComplianceMonitor.Application.Compliance.Models;

namespace ComplianceMonitor.Api.Features.Summary;

public sealed record SummaryResponse(int Total, ResultCounts ByResult)
{
    public static SummaryResponse From(AnalysisSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return new(summary.Total, new ResultCounts(summary.Complies, summary.Deviates, summary.Unclear));
    }
}

/// <summary>Explicit properties rather than a dictionary, so all three keys are always present and documented.</summary>
public sealed record ResultCounts(
    [property: JsonPropertyName("COMPLIES")] int Complies,
    [property: JsonPropertyName("DEVIATES")] int Deviates,
    [property: JsonPropertyName("UNCLEAR")] int Unclear);
