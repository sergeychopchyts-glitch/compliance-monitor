using System.Net;
using System.Text;
using System.Text.Json;

namespace ComplianceMonitor.Client.Tests;

public sealed record SentRequest(HttpMethod Method, Uri Uri, string? Body);

/// <summary>A scripted stand-in for the API: records every request and answers with the given function.</summary>
public sealed class FakeApi(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<SentRequest> Requests { get; } = [];

    /// <summary>A well-behaved API: /analyze returns <paramref name="resultFor"/>(action), plus fixed history and summary.</summary>
    public static FakeApi Healthy(Func<string, string>? resultFor = null, string history = "[]") => new((request, body) =>
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path.EndsWith("/analyze", StringComparison.Ordinal))
        {
            var action = JsonDocument.Parse(body!).RootElement.GetProperty("action").GetString()!;
            var result = resultFor?.Invoke(action) ?? "COMPLIES";
            return Json(HttpStatusCode.OK, $$"""
                {"id":7,"action":{{JsonSerializer.Serialize(action)}},"guideline":"g","result":"{{result}}","confidence":0.88,"decidedBy":"MODEL","timestamp":"2026-10-03T10:15:00Z"}
                """);
        }

        return path.EndsWith("/history", StringComparison.Ordinal)
            ? Json(HttpStatusCode.OK, history)
            : Json(HttpStatusCode.OK, """{"total":4,"byResult":{"COMPLIES":1,"DEVIATES":2,"UNCLEAR":1}}""");
    });

    /// <summary>Answers without recording, so one fake can delegate to another.</summary>
    public HttpResponseMessage Respond(HttpRequestMessage request, string? body) => respond(request, body);

    public static FakeApi Always(Func<HttpResponseMessage> response) => new((_, _) => response());

    public static FakeApi Throwing(Exception exception) => new((_, _) => throw exception);

    public static HttpResponseMessage Json(HttpStatusCode status, string body, string mediaType = "application/json") =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new SentRequest(request.Method, request.RequestUri!, body));
        return respond(request, body);
    }
}
