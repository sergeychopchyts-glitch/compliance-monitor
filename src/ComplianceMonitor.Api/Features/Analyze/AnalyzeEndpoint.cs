using ComplianceMonitor.Api.RateLimiting;
using ComplianceMonitor.Application.Compliance;
using Microsoft.AspNetCore.Http.HttpResults;

namespace ComplianceMonitor.Api.Features.Analyze;

public static class AnalyzeEndpoint
{
    public static RouteGroupBuilder MapAnalyze(this RouteGroupBuilder group)
    {
        group.MapPost("/analyze", AnalyzeAsync)
            .WithName("Analyze")
            .WithSummary("Classify an action against a guideline and store the result.")
            .RequireRateLimiting(AnalyzeRateLimitOptions.PolicyName)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .ProducesProblem(StatusCodes.Status504GatewayTimeout);
        return group;
    }

    // Model failures surface as ModelGatewayException and become ProblemDetails in ModelGatewayExceptionHandler.
    private static async Task<Ok<AnalysisResponse>> AnalyzeAsync(
        AnalyzeRequest request, IComplianceAnalysisService service, CancellationToken cancellationToken) =>
        TypedResults.Ok(AnalysisResponse.From(await service.AnalyzeAsync(request.Action, request.Guideline, cancellationToken)));
}
