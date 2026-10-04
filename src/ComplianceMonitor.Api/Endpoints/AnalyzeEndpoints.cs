using System.Text.Json;
using ComplianceMonitor.Api.Classification;
using ComplianceMonitor.Api.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;

namespace ComplianceMonitor.Api.Endpoints;

public sealed record AnalyzeRequest(string? Action, string? Guideline);

public sealed record AnalysisResponse(
    string Action, string Guideline, ComplianceResult Result, double Confidence, DateTime Timestamp);

public static class AnalyzeEndpoints
{
    public static IEndpointRouteBuilder MapAnalyzeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/analyze", AnalyzeAsync)
            .WithName("Analyze")
            .WithSummary("Classify an action against a guideline and store the result.")
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .ProducesProblem(StatusCodes.Status504GatewayTimeout);
        return app;
    }

    // Classification failures throw HuggingFaceException, which HuggingFaceExceptionHandler turns into
    // ProblemDetails; nothing is saved because AddAsync is never reached.
    private static async Task<Results<Ok<AnalysisResponse>, ValidationProblem>> AnalyzeAsync(
        AnalyzeRequest request,
        IComplianceClassifier classifier,
        AnalysisStore store,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var action = request.Action?.Trim() ?? "";
        var guideline = request.Guideline?.Trim() ?? "";
        var errors = new Dictionary<string, string[]>();
        AddErrors(errors, "action", action);
        AddErrors(errors, "guideline", guideline);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var outcome = await classifier.ClassifyAsync(action, guideline, cancellationToken);

        var record = await store.AddAsync(new AnalysisRecord
        {
            Action = action,
            Guideline = guideline,
            Result = outcome.Result,
            Confidence = outcome.Confidence,
            CreatedAt = TruncateToSeconds(timeProvider.GetUtcNow()),
            Strategy = outcome.Strategy,
            DecidedBy = outcome.DecidedBy,
            ScoresJson = JsonSerializer.Serialize(outcome.Scores, JsonSerializerOptions.Web),
        }, cancellationToken);

        return TypedResults.Ok(new AnalysisResponse(
            record.Action, record.Guideline, record.Result, record.Confidence, record.CreatedAt));
    }

    private static void AddErrors(Dictionary<string, string[]> errors, string field, string value)
    {
        if (value.Length == 0)
        {
            errors[field] = [$"The {field} is required."];
        }
        else if (value.Length > AnalysisRecord.MaxTextLength)
        {
            errors[field] = [$"The {field} must be at most {AnalysisRecord.MaxTextLength} characters."];
        }
    }

    private static DateTime TruncateToSeconds(DateTimeOffset now)
    {
        var utc = now.UtcDateTime;
        return new DateTime(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
    }
}
