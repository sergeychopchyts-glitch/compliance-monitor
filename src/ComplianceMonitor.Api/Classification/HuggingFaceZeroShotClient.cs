using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Polly.RateLimiting;
using Polly.Timeout;

namespace ComplianceMonitor.Api.Classification;

public sealed record LabelScore(string Label, double Score);

public sealed record ZeroShotRequest(
    [property: JsonPropertyName("inputs")] string Inputs,
    [property: JsonPropertyName("parameters")] ZeroShotParameters Parameters);

public sealed record ZeroShotParameters(
    [property: JsonPropertyName("candidate_labels")] IReadOnlyList<string> CandidateLabels,
    [property: JsonPropertyName("hypothesis_template")] string HypothesisTemplate,
    [property: JsonPropertyName("multi_label")] bool MultiLabel);

/// <summary>
/// Typed client for the Hugging Face zero-shot classification endpoint.
/// Retries and timeouts come from the resilience pipeline registered with it
/// (see <see cref="ClassificationServiceCollectionExtensions"/>). Logs one line per call; never the token or headers.
/// </summary>
public sealed partial class HuggingFaceZeroShotClient(
    HttpClient httpClient,
    IOptions<HuggingFaceOptions> options,
    ILogger<HuggingFaceZeroShotClient> logger,
    TimeProvider timeProvider)
{
    private readonly HttpClient _httpClient = httpClient;
    private readonly HuggingFaceOptions _options = options.Value;
    private readonly ILogger<HuggingFaceZeroShotClient> _logger = logger;
    private readonly TimeProvider _timeProvider = timeProvider;

    public async Task<IReadOnlyList<LabelScore>> ClassifyAsync(ZeroShotRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var started = _timeProvider.GetTimestamp();

        using var message = new HttpRequestMessage(HttpMethod.Post, _options.ModelUrl)
        {
            Content = JsonContent.Create(request),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiToken);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        int? status = null;
        try
        {
            using var response = await SendAsync(message, cancellationToken);

            // When the caller cancels between attempts, the retry pipeline stops and hands back the last
            // response (e.g. a 503) instead of throwing. That is a cancellation, not an upstream failure.
            cancellationToken.ThrowIfCancellationRequested();
            status = (int)response.StatusCode;

            if (!response.IsSuccessStatusCode)
            {
                throw ToException(response);
            }

            string body;
            try
            {
                body = await response.Content.ReadAsStringAsync(cancellationToken);
            }
            catch (Exception ex) when (IsTimeout(ex, cancellationToken))
            {
                throw HuggingFaceTransientException.Timeout(_options.TimeoutSeconds, ex);
            }

            var scores = Parse(body);
            LogCall(LogLevel.Information, "succeeded", status, message, started);
            return scores;
        }
        catch (HuggingFaceException ex)
        {
            var outcome = ex switch
            {
                HuggingFaceTransientException { IsTimeout: true } => "timed out",
                HuggingFaceTransientException => "failed (transient)",
                HuggingFacePermanentException { IsMalformedResponse: true } => "failed (malformed response)",
                _ => "failed (permanent)",
            };
            LogCall(LogLevel.Warning, outcome, ex.UpstreamStatusCode ?? status, message, started);
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LogCall(LogLevel.Information, "cancelled by caller", status, message, started);
            throw;
        }
    }

    private void LogCall(LogLevel level, string outcome, int? status, HttpRequestMessage message, long started)
    {
        // Counted by AttemptCountingHandler inside the retry pipeline; absent when the client is used without it.
        var attempts = message.Options.TryGetValue(AttemptCountingHandler.AttemptsKey, out var counted) && counted > 0 ? counted : 1;
        var elapsedMs = (long)_timeProvider.GetElapsedTime(started).TotalMilliseconds;
        LogHuggingFaceCall(_logger, level, outcome, status, attempts, elapsedMs);
    }

    [LoggerMessage(Message = "Hugging Face call {Outcome}: status {StatusCode}, {Attempts} attempt(s), {ElapsedMs} ms")]
    private static partial void LogHuggingFaceCall(
        ILogger logger, LogLevel level, string outcome, int? statusCode, int attempts, long elapsedMs);

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken cancellationToken)
    {
        try
        {
            return await _httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (Exception ex) when (IsTimeout(ex, cancellationToken))
        {
            throw HuggingFaceTransientException.Timeout(_options.TimeoutSeconds, ex);
        }
        catch (HttpRequestException ex)
        {
            throw HuggingFaceTransientException.Unreachable(ex);
        }
        catch (BrokenCircuitException ex)
        {
            // After repeated failures the pipeline stops calling HF for a while; that's "unavailable", not a 500.
            throw HuggingFaceTransientException.Rejected("circuit breaker open", null, ex);
        }
        catch (RateLimiterRejectedException ex)
        {
            throw HuggingFaceTransientException.Rejected("rate limit reached", ex.RetryAfter, ex);
        }
    }

    // A cancellation the caller did not ask for is a timeout (HttpClient.Timeout or the resilience pipeline).
    private static bool IsTimeout(Exception ex, CancellationToken cancellationToken) =>
        ex is TimeoutRejectedException
        || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested);

    private static HuggingFaceException ToException(HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;
        var isTransient = status is 408 or 429 || status >= 500;
        return isTransient
            ? HuggingFaceTransientException.FromStatus(status, response.Headers.RetryAfter?.Delta)
            : HuggingFacePermanentException.FromStatus(status);
    }

    /// <summary>
    /// Accepts the current router shape <c>[{"label", "score"}]</c> (docs/hf-sample-response.json)
    /// and the legacy shape <c>{"labels": [], "scores": []}</c>. Anything else is malformed.
    /// </summary>
    private static List<LabelScore> Parse(string body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            // The JsonException message can quote the body, so it is not kept as the inner exception.
            throw HuggingFacePermanentException.Malformed("body is not valid JSON");
        }

        using (document)
        {
            var root = document.RootElement;
            var scores = root.ValueKind switch
            {
                JsonValueKind.Array => ParseCurrentShape(root),
                JsonValueKind.Object => ParseLegacyShape(root),
                _ => throw HuggingFacePermanentException.Malformed("unexpected JSON root"),
            };

            if (scores.Count == 0)
            {
                throw HuggingFacePermanentException.Malformed("no labels returned");
            }

            return scores;
        }
    }

    private static List<LabelScore> ParseCurrentShape(JsonElement root)
    {
        var scores = new List<LabelScore>();
        foreach (var item in root.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("label", out var label)
                || !item.TryGetProperty("score", out var score))
            {
                throw HuggingFacePermanentException.Malformed("array item is not {label, score}");
            }

            scores.Add(ToLabelScore(label, score));
        }

        return scores;
    }

    private static List<LabelScore> ParseLegacyShape(JsonElement root)
    {
        if (!root.TryGetProperty("labels", out var labels) || labels.ValueKind != JsonValueKind.Array
            || !root.TryGetProperty("scores", out var scores) || scores.ValueKind != JsonValueKind.Array)
        {
            throw HuggingFacePermanentException.Malformed("object has no labels and scores arrays");
        }

        if (labels.GetArrayLength() != scores.GetArrayLength())
        {
            throw HuggingFacePermanentException.Malformed("labels and scores differ in length");
        }

        return labels.EnumerateArray().Zip(scores.EnumerateArray(), ToLabelScore).ToList();
    }

    private static LabelScore ToLabelScore(JsonElement label, JsonElement score)
    {
        if (label.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(label.GetString()))
        {
            throw HuggingFacePermanentException.Malformed("label is not a non-empty string");
        }

        if (score.ValueKind != JsonValueKind.Number || !score.TryGetDouble(out var value)
            || !double.IsFinite(value) || value is < 0 or > 1)
        {
            throw HuggingFacePermanentException.Malformed("score is not a number between 0 and 1");
        }

        return new LabelScore(label.GetString()!, value);
    }
}
