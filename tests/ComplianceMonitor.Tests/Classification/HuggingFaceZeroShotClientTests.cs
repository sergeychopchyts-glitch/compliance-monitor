using System.Net;
using System.Text.Json.Nodes;
using ComplianceMonitor.Api.Classification;
using ComplianceMonitor.Tests.Infrastructure;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Polly.RateLimiting;
using Polly.Timeout;

namespace ComplianceMonitor.Tests.Classification;

public sealed class HuggingFaceZeroShotClientTests
{
    private const string Token = "hf_test_secret_token";
    private const string Inputs = "Action: Closed ticket #48219\nGuideline: All closed tickets must include a confirmation email";

    private static readonly ZeroShotRequest Request = new(
        Inputs,
        new ZeroShotParameters(["complies with the guideline", "violates the guideline"], "This action {}.", false));

    private static HuggingFaceZeroShotClient CreateClient(HttpMessageHandler handler) =>
        TestClients.HuggingFace(handler, new HuggingFaceOptions { ApiToken = Token });

    private static Task<IReadOnlyList<LabelScore>> Classify(HttpMessageHandler handler) =>
        CreateClient(handler).ClassifyAsync(Request, TestContext.Current.CancellationToken);

    [Fact]
    public async Task ClassifyAsync_PostsToModelUrlWithBearerTokenAndExactBody()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.OK, """[{"label":"violates the guideline","score":1}]""");

        await Classify(handler);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal(new Uri("https://router.huggingface.co/hf-inference/models/facebook/bart-large-mnli"), sent.Uri);
        Assert.Equal("Bearer", sent.Authorization?.Scheme);
        Assert.Equal(Token, sent.Authorization?.Parameter);
        Assert.Equal("application/json", sent.ContentType);
        var expected = JsonNode.Parse("""
            {
              "inputs": "Action: Closed ticket #48219\nGuideline: All closed tickets must include a confirmation email",
              "parameters": {
                "candidate_labels": ["complies with the guideline", "violates the guideline"],
                "hypothesis_template": "This action {}.",
                "multi_label": false
              }
            }
            """);
        Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(sent.Body!)), $"Unexpected body: {sent.Body}");
    }

    [Fact]
    public async Task ClassifyAsync_ParsesRecordedSampleResponse()
    {
        var sample = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "TestData", "hf-sample-response.json"), TestContext.Current.CancellationToken);

        var scores = await Classify(FakeHttpMessageHandler.Returning(HttpStatusCode.OK, sample));

        Assert.Equal(
            [new LabelScore("complies with the guideline", 0.9238446950912476), new LabelScore("violates the guideline", 0.07615531980991364)],
            scores);
    }

    [Fact]
    public async Task ClassifyAsync_ParsesLegacyLabelsAndScoresShape()
    {
        const string legacy = """
            {"sequence":"x","labels":["violates the guideline","complies with the guideline"],"scores":[0.7,0.3]}
            """;

        var scores = await Classify(FakeHttpMessageHandler.Returning(HttpStatusCode.OK, legacy));

        Assert.Equal(
            [new LabelScore("violates the guideline", 0.7), new LabelScore("complies with the guideline", 0.3)],
            scores);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("42")]
    [InlineData("[]")]
    [InlineData("""{"error":"Model facebook/bart-large-mnli is currently loading"}""")]
    [InlineData("""[{"label":"complies with the guideline"}]""")]
    [InlineData("""[{"label":1,"score":0.5}]""")]
    [InlineData("""[{"label":"","score":0.5}]""")]
    [InlineData("""[{"label":"x","score":"high"}]""")]
    [InlineData("""[{"label":"x","score":1.5}]""")]
    [InlineData("""[{"label":"x","score":-0.1}]""")]
    [InlineData("""["complies with the guideline"]""")]
    [InlineData("""{"labels":["a","b"],"scores":[0.5]}""")]
    [InlineData("""{"labels":"a","scores":[0.5]}""")]
    public async Task ClassifyAsync_MalformedBody_ThrowsPermanentMalformed(string body)
    {
        var ex = await Assert.ThrowsAsync<HuggingFacePermanentException>(
            () => Classify(FakeHttpMessageHandler.Returning(HttpStatusCode.OK, body)));

        Assert.True(ex.IsMalformedResponse);
        Assert.Null(ex.UpstreamStatusCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task ClassifyAsync_TransientStatus_ThrowsTransientWithStatus(HttpStatusCode status)
    {
        var ex = await Assert.ThrowsAsync<HuggingFaceTransientException>(
            () => Classify(FakeHttpMessageHandler.Returning(status, $$"""{"error":"{{Token}} {{Inputs}}"}""")));

        Assert.Equal((int)status, ex.UpstreamStatusCode);
        Assert.False(ex.IsTimeout);
        Assert.Equal($"Hugging Face returned HTTP {(int)status}.", ex.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.PaymentRequired)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task ClassifyAsync_PermanentStatus_ThrowsPermanentWithStatus(HttpStatusCode status)
    {
        var ex = await Assert.ThrowsAsync<HuggingFacePermanentException>(
            () => Classify(FakeHttpMessageHandler.Returning(status, $$"""{"error":"{{Token}} {{Inputs}}"}""")));

        Assert.Equal((int)status, ex.UpstreamStatusCode);
        Assert.False(ex.IsMalformedResponse);
        Assert.Equal($"Hugging Face returned HTTP {(int)status}.", ex.Message);
    }

    [Fact]
    public async Task ClassifyAsync_429WithRetryAfter_CarriesDelay()
    {
        var handler = new FakeHttpMessageHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(20));
            return response;
        });

        var ex = await Assert.ThrowsAsync<HuggingFaceTransientException>(() => Classify(handler));

        Assert.Equal(TimeSpan.FromSeconds(20), ex.RetryAfter);
    }

    public static TheoryData<Exception> TimeoutExceptions =>
    [
        new TaskCanceledException("HttpClient.Timeout", new TimeoutException()),
        new TimeoutRejectedException(),
    ];

    [Theory]
    [MemberData(nameof(TimeoutExceptions))]
    public async Task ClassifyAsync_Timeout_ThrowsTransientTimeout(Exception thrown)
    {
        var ex = await Assert.ThrowsAsync<HuggingFaceTransientException>(
            () => Classify(FakeHttpMessageHandler.Throwing(thrown)));

        Assert.True(ex.IsTimeout);
        Assert.Null(ex.UpstreamStatusCode);
    }

    [Fact]
    public async Task ClassifyAsync_CircuitBreakerOpen_ThrowsTransientNotUnhandled()
    {
        var ex = await Assert.ThrowsAsync<HuggingFaceTransientException>(
            () => Classify(FakeHttpMessageHandler.Throwing(new BrokenCircuitException())));

        Assert.False(ex.IsTimeout);
        Assert.Null(ex.UpstreamStatusCode);
        Assert.Equal("Calls to Hugging Face are temporarily suspended (circuit breaker open).", ex.Message);
    }

    [Fact]
    public async Task ClassifyAsync_RateLimiterRejected_ThrowsTransientWithRetryAfter()
    {
        var ex = await Assert.ThrowsAsync<HuggingFaceTransientException>(
            () => Classify(FakeHttpMessageHandler.Throwing(new RateLimiterRejectedException(TimeSpan.FromSeconds(5)))));

        Assert.Equal(TimeSpan.FromSeconds(5), ex.RetryAfter);
        Assert.Equal("Calls to Hugging Face are temporarily suspended (rate limit reached).", ex.Message);
    }

    [Fact]
    public async Task ClassifyAsync_NetworkFailure_ThrowsTransientUnreachable()
    {
        var ex = await Assert.ThrowsAsync<HuggingFaceTransientException>(
            () => Classify(FakeHttpMessageHandler.Throwing(new HttpRequestException("Connection refused"))));

        Assert.False(ex.IsTimeout);
        Assert.Null(ex.UpstreamStatusCode);
    }

    [Fact]
    public async Task ClassifyAsync_CallerCancels_PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        var handler = new FakeHttpMessageHandler(_ =>
        {
            cts.Cancel();
            throw new TaskCanceledException();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateClient(handler).ClassifyAsync(Request, cts.Token));
    }
}
