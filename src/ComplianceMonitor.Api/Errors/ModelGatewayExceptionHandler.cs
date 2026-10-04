using System.Globalization;
using ComplianceMonitor.Application.Errors;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace ComplianceMonitor.Api.Errors;

/// <summary>
/// Turns model failures into RFC 7807 responses. Knows only the provider-neutral <see cref="ModelGatewayException"/>,
/// so changing the model provider doesn't touch the HTTP layer. Other exceptions fall through to a generic 500.
/// </summary>
public sealed partial class ModelGatewayExceptionHandler(
    IProblemDetailsService problemDetails, ILogger<ModelGatewayExceptionHandler> logger) : IExceptionHandler
{
    public const string TypePrefix = "urn:compliance-monitor:problem:";

    private readonly IProblemDetailsService _problemDetails = problemDetails;
    private readonly ILogger<ModelGatewayExceptionHandler> _logger = logger;

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not ModelGatewayException failure)
        {
            return false;
        }

        var (status, type, title) = Describe(failure.Kind);
        LogFailure(_logger, failure.Kind, status);
        httpContext.Response.StatusCode = status;
        if (failure.RetryAfter is { } retryAfter)
        {
            httpContext.Response.Headers.RetryAfter = Math.Ceiling(retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        }

        // Too long for the model is the caller's to fix: a validation error naming both fields, like any other 400.
        ProblemDetails problem = failure.Kind == ModelGatewayFailureKind.InputTooLong
            ? new HttpValidationProblemDetails(new Dictionary<string, string[]>
            {
                ["action"] = [failure.Message],
                ["guideline"] = [failure.Message],
            })
            { Status = status, Title = title }
            : new ProblemDetails { Status = status, Type = TypePrefix + type, Title = title, Detail = failure.Message };

        return await _problemDetails.TryWriteAsync(new ProblemDetailsContext { HttpContext = httpContext, Exception = exception, ProblemDetails = problem });
    }

    public static (int Status, string Type, string Title) Describe(ModelGatewayFailureKind kind) => kind switch
    {
        ModelGatewayFailureKind.InputTooLong =>
            (StatusCodes.Status400BadRequest, "input-too-long", "One or more validation errors occurred."),
        ModelGatewayFailureKind.Timeout =>
            (StatusCodes.Status504GatewayTimeout, "classifier-timeout", "The classifier did not respond in time."),
        ModelGatewayFailureKind.Unavailable =>
            (StatusCodes.Status503ServiceUnavailable, "classifier-unavailable", "The classifier is temporarily unavailable."),
        ModelGatewayFailureKind.CreditsExhausted =>
            (StatusCodes.Status503ServiceUnavailable, "classifier-credits-exhausted", "The classifier's inference credits are exhausted."),
        ModelGatewayFailureKind.InvalidResponse =>
            (StatusCodes.Status502BadGateway, "classifier-bad-response", "The classifier returned a response that could not be used."),
        _ =>
            (StatusCodes.Status502BadGateway, "classifier-rejected", "The classifier rejected the request."),
    };

    [LoggerMessage(Level = LogLevel.Warning, Message = "Model call failed: {Kind}, responding {Status}")]
    private static partial void LogFailure(ILogger logger, ModelGatewayFailureKind kind, int status);
}
