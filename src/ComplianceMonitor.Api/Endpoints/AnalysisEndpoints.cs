using System.Text.Json;
using System.Text.Json.Serialization;
using ComplianceMonitor.Api.Classification;
using ComplianceMonitor.Api.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;

namespace ComplianceMonitor.Api.Endpoints;

public sealed record AnalyzeRequest(string? Action, string? Guideline);

/// <param name="Confidence">Rounded to 2 decimals; the database keeps full precision.</param>
/// <param name="Timestamp">UTC, serialized with a trailing Z.</param>
public sealed record AnalysisResponse(
    long Id,
    string Action,
    string Guideline,
    ComplianceResult Result,
    double Confidence,
    DecisionSource DecidedBy,
    DateTime Timestamp)
{
    public static AnalysisResponse From(AnalysisRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new(
            record.Id,
            record.Action,
            record.Guideline,
            record.Result,
            Math.Round(record.Confidence, 2, MidpointRounding.AwayFromZero),
            record.DecidedBy,
            record.CreatedAt);
    }
}

public sealed record SummaryResponse(int Total, ResultCounts ByResult);

/// <summary>Explicit properties rather than a dictionary, so all three keys are always present and documented.</summary>
public sealed record ResultCounts(
    [property: JsonPropertyName("COMPLIES")] int Complies,
    [property: JsonPropertyName("DEVIATES")] int Deviates,
    [property: JsonPropertyName("UNCLEAR")] int Unclear);

public static class AnalysisEndpoints
{
    public const int MaxHistoryLimit = 200;
    public const int DefaultHistoryLimit = 50;

    public static IEndpointRouteBuilder MapAnalysisEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("").WithTags("Analysis");

        group.MapPost("/analyze", AnalyzeAsync)
            .WithName("Analyze")
            .WithSummary("Classify an action against a guideline and store the result.")
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .ProducesProblem(StatusCodes.Status504GatewayTimeout);

        group.MapGet("/history", GetHistoryAsync)
            .WithName("History")
            .WithSummary("Stored analyses, newest first.");

        group.MapGet("/summary", GetSummaryAsync)
            .WithName("Summary")
            .WithSummary("Totals over all stored analyses.");

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
        ValidateText(errors, "action", action);
        ValidateText(errors, "guideline", guideline);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var input = classifier.CheckInput(action, guideline);
        if (!input.Fits)
        {
            string[] tooLong =
            [
                $"The action and guideline together are too long for the model: they may need up to {input.TokenUpperBound} " +
                $"tokens and it reads {input.MaxTokens}. Shorten either one.",
            ];
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["action"] = tooLong, ["guideline"] = tooLong });
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

        return TypedResults.Ok(AnalysisResponse.From(record));
    }

    /// <param name="limit">1–200, default 50.</param>
    /// <param name="offset">0 or more, default 0.</param>
    /// <param name="result">Optional filter: COMPLIES, DEVIATES or UNCLEAR.</param>
    private static async Task<Results<Ok<AnalysisResponse[]>, ValidationProblem>> GetHistoryAsync(
        AnalysisStore store,
        CancellationToken cancellationToken,
        int? limit = null,
        int? offset = null,
        string? result = null)
    {
        var errors = new Dictionary<string, string[]>();
        if (limit is < 1 or > MaxHistoryLimit)
        {
            errors["limit"] = [$"The limit must be between 1 and {MaxHistoryLimit}."];
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

        var records = await store.GetHistoryAsync(limit ?? DefaultHistoryLimit, offset ?? 0, filter, cancellationToken);
        return TypedResults.Ok(records.Select(AnalysisResponse.From).ToArray());
    }

    private static async Task<Ok<SummaryResponse>> GetSummaryAsync(AnalysisStore store, CancellationToken cancellationToken)
    {
        var summary = await store.GetSummaryAsync(cancellationToken);
        return TypedResults.Ok(new SummaryResponse(
            summary.Total,
            new ResultCounts(
                summary.ByResult[ComplianceResult.Complies],
                summary.ByResult[ComplianceResult.Deviates],
                summary.ByResult[ComplianceResult.Unclear])));
    }

    private static void ValidateText(Dictionary<string, string[]> errors, string field, string value)
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

    // Accepts the API names only (case-insensitive), never numbers or C# names like "Complies1".
    private static ComplianceResult? ParseResult(string text) =>
        Enum.GetValues<ComplianceResult>()
            .Select(r => (ComplianceResult?)r)
            .FirstOrDefault(r => string.Equals(EnumText.ToText(r!.Value), text.Trim(), StringComparison.OrdinalIgnoreCase));

    private static DateTime TruncateToSeconds(DateTimeOffset now)
    {
        var utc = now.UtcDateTime;
        return new DateTime(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
    }
}
