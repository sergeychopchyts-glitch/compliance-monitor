using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
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
/// Retries and the overall timeout come from the resilience handler registered with it.
/// </summary>
public sealed class HuggingFaceZeroShotClient(HttpClient httpClient, IOptions<HuggingFaceOptions> options)
{
    private readonly HttpClient _httpClient = httpClient;
    private readonly HuggingFaceOptions _options = options.Value;

    public async Task<IReadOnlyList<LabelScore>> ClassifyAsync(ZeroShotRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var message = new HttpRequestMessage(HttpMethod.Post, _options.ModelUrl)
        {
            Content = JsonContent.Create(request),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiToken);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await SendAsync(message, cancellationToken);

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

        return Parse(body);
    }

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
