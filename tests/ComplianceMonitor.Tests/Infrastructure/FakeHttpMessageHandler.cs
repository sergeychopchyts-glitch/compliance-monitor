using System.Net;
using System.Text;

namespace ComplianceMonitor.Tests.Infrastructure;

/// <summary>Records every request (with its body read at send time) and answers with a scripted response.</summary>
public sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond = respond;

    public List<RecordedRequest> Requests { get; } = [];

    public static FakeHttpMessageHandler Returning(HttpStatusCode status, string body = "") =>
        new(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });

    public static FakeHttpMessageHandler Throwing(Exception exception) => new(_ => throw exception);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new RecordedRequest(request.Method, request.RequestUri, request.Headers.Authorization, request.Content?.Headers.ContentType?.MediaType, body));
        return _respond(request);
    }
}

public sealed record RecordedRequest(
    HttpMethod Method,
    Uri? Uri,
    System.Net.Http.Headers.AuthenticationHeaderValue? Authorization,
    string? ContentType,
    string? Body);
