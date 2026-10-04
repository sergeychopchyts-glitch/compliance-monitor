using System.Globalization;
using ComplianceMonitor.Api.Classification;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace ComplianceMonitor.Api.Errors;

/// <summary>
/// Turns classification failures into RFC 7807 responses (docs/plan.md, section 4).
/// Other exceptions fall through to the default 500 ProblemDetails.
/// </summary>
public sealed partial class HuggingFaceExceptionHandler(
    IProblemDetailsService problemDetails, ILogger<HuggingFaceExceptionHandler> logger) : IExceptionHandler
{
    public const string TypePrefix = "urn:compliance-monitor:problem:";

    private readonly IProblemDetailsService _problemDetails = problemDetails;
    private readonly ILogger<HuggingFaceExceptionHandler> _logger = logger;

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not HuggingFaceException hf)
        {
            return false;
        }

        var (status, type, title) = Describe(hf);
        LogFailure(_logger, hf.GetType().Name, hf.UpstreamStatusCode, status);

        httpContext.Response.StatusCode = status;
        if (hf is HuggingFaceTransientException { RetryAfter: { } retryAfter })
        {
            httpContext.Response.Headers.RetryAfter =
                Math.Ceiling(retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        }

        return await _problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Type = TypePrefix + type,
                Title = title,
                // Exception messages carry only the upstream status code (see HuggingFaceException).
                Detail = hf.Message,
            },
        });
    }

    public static (int Status, string Type, string Title) Describe(HuggingFaceException exception) => exception switch
    {
        HuggingFaceTransientException { IsTimeout: true } =>
            (StatusCodes.Status504GatewayTimeout, "classifier-timeout", "The classifier did not respond in time."),
        HuggingFaceTransientException =>
            (StatusCodes.Status503ServiceUnavailable, "classifier-unavailable", "The classifier is temporarily unavailable."),
        HuggingFacePermanentException { UpstreamStatusCode: 402 } =>
            (StatusCodes.Status503ServiceUnavailable, "classifier-credits-exhausted", "The classifier's inference credits are exhausted."),
        HuggingFacePermanentException { IsMalformedResponse: true } =>
            (StatusCodes.Status502BadGateway, "classifier-bad-response", "The classifier returned a response that could not be used."),
        _ =>
            (StatusCodes.Status502BadGateway, "classifier-rejected", "The classifier rejected the request."),
    };

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Classification failed: {ExceptionType}, upstream status {UpstreamStatus}, responding {Status}")]
    private static partial void LogFailure(ILogger logger, string exceptionType, int? upstreamStatus, int status);
}
