using ComplianceMonitor.Application.Compliance;
using Microsoft.AspNetCore.Http.HttpResults;

namespace ComplianceMonitor.Api.Features.Summary;

public static class SummaryEndpoint
{
    public static RouteGroupBuilder MapSummary(this RouteGroupBuilder group)
    {
        group.MapGet("/summary", GetSummaryAsync)
            .WithName("Summary")
            .WithSummary("Totals over all stored analyses.");
        return group;
    }

    private static async Task<Ok<SummaryResponse>> GetSummaryAsync(IComplianceAnalysisService service, CancellationToken cancellationToken) =>
        TypedResults.Ok(SummaryResponse.From(await service.GetSummaryAsync(cancellationToken)));
}
