using System.Net.Http.Json;
using System.Text.Json;

namespace ComplianceMonitor.Client;

// The client's own view of the API contract (docs/plan.md, section 4); it does not reference the API project.
public sealed record AnalysisItem(
    long Id, string Action, string Guideline, string Result, double Confidence, string DecidedBy, string Timestamp);

public sealed record Summary(int Total, IReadOnlyDictionary<string, int> ByResult);

/// <summary>The API could not be reached, or did not answer in time. No HTTP response exists.</summary>
public sealed class ApiUnreachableException(Uri baseUrl, bool timedOut, Exception innerException)
    : Exception(timedOut ? $"The API at {baseUrl} did not respond in time." : $"Can't reach the API at {baseUrl}.", innerException)
{
    public Uri BaseUrl { get; } = baseUrl;

    public bool TimedOut { get; } = timedOut;
}

/// <summary>The API answered with an error, or with a body the client could not read.</summary>
public sealed class ApiErrorException(
    int statusCode,
    string? title,
    string? detail,
    IReadOnlyDictionary<string, string[]> fieldErrors,
    TimeSpan? retryAfter)
    : Exception($"The API returned {statusCode}.")
{
    public int StatusCode { get; } = statusCode;

    /// <summary>Null when the body was not ProblemDetails JSON.</summary>
    public string? Title { get; } = title;

    public string? Detail { get; } = detail;

    public IReadOnlyDictionary<string, string[]> FieldErrors { get; } = fieldErrors;

    public TimeSpan? RetryAfter { get; } = retryAfter;

    public static ApiErrorException Unreadable(int statusCode) => new(statusCode, null, null, new Dictionary<string, string[]>(), null);
}

/// <summary>Typed client for the Compliance Monitor API. Turns every failure into one of two exceptions.</summary>
public sealed class ComplianceApiClient(HttpClient httpClient)
{
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    private readonly HttpClient _httpClient = httpClient;

    public Uri BaseUrl => _httpClient.BaseAddress ?? throw new InvalidOperationException("HttpClient.BaseAddress is not set.");

    public Task<AnalysisItem> AnalyzeAsync(string action, string guideline, CancellationToken cancellationToken) =>
        SendAsync<AnalysisItem>(
            new HttpRequestMessage(HttpMethod.Post, "analyze") { Content = JsonContent.Create(new { action, guideline }, options: Json) },
            cancellationToken);

    public Task<AnalysisItem[]> GetHistoryAsync(int? limit, string? result, CancellationToken cancellationToken)
    {
        var query = new List<string>();
        if (limit is { } l)
        {
            query.Add($"limit={l}");
        }

        if (result is not null)
        {
            query.Add($"result={Uri.EscapeDataString(result)}");
        }

        var path = query.Count == 0 ? "history" : "history?" + string.Join('&', query);
        return SendAsync<AnalysisItem[]>(new HttpRequestMessage(HttpMethod.Get, path), cancellationToken);
    }

    public Task<Summary> GetSummaryAsync(CancellationToken cancellationToken) =>
        SendAsync<Summary>(new HttpRequestMessage(HttpMethod.Get, "summary"), cancellationToken);

    private async Task<T> SendAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var _ = request;
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ApiUnreachableException(BaseUrl, timedOut: true, ex); // HttpClient.Timeout
        }
        catch (HttpRequestException ex)
        {
            throw new ApiUnreachableException(BaseUrl, timedOut: false, ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw ToError(response, body);
            }

            try
            {
                return JsonSerializer.Deserialize<T>(body, Json) ?? throw ApiErrorException.Unreadable((int)response.StatusCode);
            }
            catch (JsonException)
            {
                throw ApiErrorException.Unreadable((int)response.StatusCode);
            }
        }
    }

    private static ApiErrorException ToError(HttpResponseMessage response, string body)
    {
        var status = (int)response.StatusCode;
        var retryAfter = response.Headers.RetryAfter?.Delta;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return ApiErrorException.Unreadable(status);
            }

            var fieldErrors = new Dictionary<string, string[]>();
            if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
            {
                foreach (var field in errors.EnumerateObject())
                {
                    fieldErrors[field.Name] = field.Value.ValueKind == JsonValueKind.Array
                        ? [.. field.Value.EnumerateArray().Select(e => e.ToString())]
                        : [field.Value.ToString()];
                }
            }

            var title = String(root, "title");
            var detail = String(root, "detail");
            return title is null && detail is null && fieldErrors.Count == 0
                ? ApiErrorException.Unreadable(status)
                : new ApiErrorException(status, title, detail, fieldErrors, retryAfter);
        }
        catch (JsonException)
        {
            return ApiErrorException.Unreadable(status);
        }

        static string? String(JsonElement root, string name) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}

/// <summary>How the client is configured from the outside world; separate so tests can control it.</summary>
public static class ClientDefaults
{
    /// <summary>Must match the "http" profile in src/ComplianceMonitor.Api/Properties/launchSettings.json (a test checks it).</summary>
    public static readonly Uri BaseUrl = new("http://localhost:5080");

    public const string BaseUrlVariable = "COMPLIANCE_API_URL";

    /// <summary>Above the API's own 30 s budget for Hugging Face, so the API's 504 arrives before the client gives up.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
}
