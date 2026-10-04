using ComplianceMonitor.Api.Contracts;
using ComplianceMonitor.Application.Compliance;
using ComplianceMonitor.Application.Compliance.Models;
using Microsoft.AspNetCore.Http.HttpResults;

namespace ComplianceMonitor.Api.Features.History;

public static class HistoryEndpoint
{
    public const int MaxLimit = 200;
    public const int DefaultLimit = 50;

    public static RouteGroupBuilder MapHistory(this RouteGroupBuilder group)
    {
        group.MapGet("/history", GetHistoryAsync)
            .WithName("History")
            .WithSummary("Stored analyses, newest first.");
        return group;
    }

    // Query-string rules stay explicit here: nullable numbers and an enum filter read more clearly than attributes.
    /// <param name="limit">1–200, default 50.</param>
    /// <param name="offset">0 or more, default 0.</param>
    /// <param name="result">Optional filter: COMPLIES, DEVIATES or UNCLEAR.</param>
    private static async Task<Results<Ok<AnalysisResponse[]>, ValidationProblem>> GetHistoryAsync(
        IComplianceAnalysisService service,
        CancellationToken cancellationToken,
        int? limit = null,
        int? offset = null,
        string? result = null)
    {
        var errors = new Dictionary<string, string[]>();
        if (limit is < 1 or > MaxLimit)
        {
            errors["limit"] = [$"The limit must be between 1 and {MaxLimit}."];
        }

        if (offset is < 0)
        {
            errors["offset"] = ["The offset must be 0 or more."];
        }

        ComplianceResult? filter = null;
        if (result is not null)
        {
            filter = ParseResult(result);
            if (filter is null)
            {
                errors["result"] = ["The result must be COMPLIES, DEVIATES or UNCLEAR."];
            }
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var items = await service.GetHistoryAsync(limit ?? DefaultLimit, offset ?? 0, filter, cancellationToken);
        return TypedResults.Ok(items.Select(AnalysisResponse.From).ToArray());
    }

    // The API names only (any case); never numbers, and never the C# names.
    private static ComplianceResult? ParseResult(string text) => text.Trim().ToUpperInvariant() switch
    {
        "COMPLIES" => ComplianceResult.Complies,
        "DEVIATES" => ComplianceResult.Deviates,
        "UNCLEAR" => ComplianceResult.Unclear,
        _ => null,
    };
}
