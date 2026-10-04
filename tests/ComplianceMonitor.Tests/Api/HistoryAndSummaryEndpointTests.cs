using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ComplianceMonitor.Application.Compliance.Models;
using ComplianceMonitor.Application.Errors;
using ComplianceMonitor.Tests.TestSupport;

namespace ComplianceMonitor.Tests.Api;

public sealed class HistoryAndSummaryEndpointTests
{
    private static readonly DateTime T0 = new(2026, 10, 3, 10, 0, 0, DateTimeKind.Utc);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static NewAnalysis Record(int minute, ComplianceResult result = ComplianceResult.Complies) => new(
        $"action {minute}",
        "g",
        new ComplianceDecision(result, 0.876, DecisionSource.Model, DecisionReason.ModelClassification),
        FakeModelGateway.Scores(result, 0.876),
        0.5,
        T0.AddMinutes(minute));

    private static async Task<(HttpStatusCode Status, JsonNode Body, string? MediaType)> Get(ApiFactory factory, string path)
    {
        using var response = await factory.CreateClient().GetAsync(new Uri(path, UriKind.Relative), Ct);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;
        return (response.StatusCode, body, response.Content.Headers.ContentType?.MediaType);
    }

    private static IEnumerable<string?> Actions(JsonNode body) => body.AsArray().Select(item => (string?)item!["action"]);

    [Fact]
    public async Task History_EmptyDatabase_ReturnsEmptyArray()
    {
        using var factory = new ApiFactory();

        var (status, body, _) = await Get(factory, "/history");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Empty(body.AsArray());
    }

    [Fact]
    public async Task History_ItemsHaveAnalyzeShape()
    {
        using var factory = new ApiFactory();
        await factory.SeedAsync(Record(5, ComplianceResult.Deviates));

        var (_, body, _) = await Get(factory, "/history");

        var expected = JsonNode.Parse("""
            [{
              "id": 1,
              "action": "action 5",
              "guideline": "g",
              "result": "DEVIATES",
              "confidence": 0.88,
              "decisionSource": "MODEL",
              "decisionReason": "MODEL_CLASSIFICATION",
              "timestamp": "2026-10-03T10:05:00Z"
            }]
            """);
        Assert.True(JsonNode.DeepEquals(expected, body), body.ToJsonString());
    }

    [Fact]
    public async Task History_AfterAnalyzeCalls_IsNewestFirst()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        foreach (var action in new[] { "first", "second", "third" })
        {
            using var _ = await client.PostAsJsonAsync(new Uri("/analyze", UriKind.Relative), new { action, guideline = "g" }, Ct);
            factory.Time.Advance(TimeSpan.FromMinutes(1));
        }

        var (_, body, _) = await Get(factory, "/history");

        Assert.Equal(["third", "second", "first"], Actions(body));
    }

    [Fact]
    public async Task History_DefaultLimitIs50()
    {
        using var factory = new ApiFactory();
        await factory.SeedAsync([.. Enumerable.Range(0, 51).Select(i => Record(i))]);

        var (_, body, _) = await Get(factory, "/history");

        Assert.Equal(50, body.AsArray().Count);
        Assert.Equal("action 50", (string?)body[0]!["action"]);
    }

    [Theory]
    [InlineData("/history?limit=2", new[] { "action 4", "action 3" })]
    [InlineData("/history?limit=2&offset=2", new[] { "action 2", "action 1" })]
    [InlineData("/history?offset=4", new[] { "action 0" })]
    [InlineData("/history?offset=5", new string[0])]
    [InlineData("/history?limit=200", new[] { "action 4", "action 3", "action 2", "action 1", "action 0" })]
    public async Task History_AppliesLimitAndOffset(string path, string[] expected)
    {
        using var factory = new ApiFactory();
        await factory.SeedAsync([.. Enumerable.Range(0, 5).Select(i => Record(i))]);

        var (status, body, _) = await Get(factory, path);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(expected, Actions(body));
    }

    [Theory]
    [InlineData("DEVIATES")]
    [InlineData("deviates")]
    public async Task History_FiltersByResult(string result)
    {
        using var factory = new ApiFactory();
        await factory.SeedAsync(
            Record(0, ComplianceResult.Deviates),
            Record(1, ComplianceResult.Complies),
            Record(2, ComplianceResult.Deviates),
            Record(3, ComplianceResult.Unclear));

        var (_, body, _) = await Get(factory, $"/history?result={result}");

        Assert.Equal(["action 2", "action 0"], Actions(body));
        Assert.All(body.AsArray(), item => Assert.Equal("DEVIATES", (string?)item!["result"]));
    }

    [Theory]
    [InlineData("/history?limit=0", "limit")]
    [InlineData("/history?limit=201", "limit")]
    [InlineData("/history?limit=-5", "limit")]
    [InlineData("/history?offset=-1", "offset")]
    [InlineData("/history?result=MAYBE", "result")]
    [InlineData("/history?result=1", "result")]
    [InlineData("/history?result=Complies1", "result")]
    [InlineData("/history?result=", "result")]
    public async Task History_InvalidQuery_Returns400NamingTheField(string path, string field)
    {
        using var factory = new ApiFactory();

        var (status, body, mediaType) = await Get(factory, path);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("application/problem+json", mediaType);
        Assert.NotNull(body["errors"]?[field]);
    }

    [Theory]
    [InlineData("/history?limit=abc")]
    [InlineData("/history?offset=1.5")]
    public async Task History_UnparsableNumber_Returns400ProblemDetails(string path)
    {
        // Binding failures come from the framework, which does not name the parameter outside Development.
        // Out-of-range numbers are ours and do name it (History_InvalidQuery_Returns400NamingTheField).
        using var factory = new ApiFactory();

        var (status, body, mediaType) = await Get(factory, path);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("application/problem+json", mediaType);
        Assert.Equal(400, (int?)body["status"]);
        Assert.False(string.IsNullOrEmpty((string?)body["title"]));
        Assert.False(string.IsNullOrEmpty((string?)body["type"]));
    }

    [Fact]
    public async Task Summary_EmptyDatabase_ReturnsAllThreeKeysAtZero()
    {
        using var factory = new ApiFactory();

        var (status, body, _) = await Get(factory, "/summary");

        Assert.Equal(HttpStatusCode.OK, status);
        var expected = JsonNode.Parse("""{ "total": 0, "byResult": { "COMPLIES": 0, "DEVIATES": 0, "UNCLEAR": 0 } }""");
        Assert.True(JsonNode.DeepEquals(expected, body), body.ToJsonString());
    }

    [Fact]
    public async Task Summary_AfterMixedAnalyzeCalls_CountsEachResult()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var results = new[] { ComplianceResult.Complies, ComplianceResult.Deviates, ComplianceResult.Deviates, ComplianceResult.Unclear };
        foreach (var result in results)
        {
            factory.Model.Evaluation = FakeModelGateway.Scores(result, 0.9);
            using var _ = await client.PostAsJsonAsync(new Uri("/analyze", UriKind.Relative), new { action = result.ToString(), guideline = "g" }, Ct);
        }

        // A failed classification must not count.
        factory.Model.Throws = new ModelGatewayException(ModelGatewayFailureKind.Unavailable, "down");
        using (var failed = await client.PostAsJsonAsync(new Uri("/analyze", UriKind.Relative), new { action = "x", guideline = "g" }, Ct))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        }

        var (_, body, _) = await Get(factory, "/summary");

        var expected = JsonNode.Parse("""{ "total": 4, "byResult": { "COMPLIES": 1, "DEVIATES": 2, "UNCLEAR": 1 } }""");
        Assert.True(JsonNode.DeepEquals(expected, body), body.ToJsonString());
    }

    [Fact]
    public async Task Summary_MissingResult_IsZeroNotAbsent()
    {
        using var factory = new ApiFactory();
        await factory.SeedAsync(Record(0, ComplianceResult.Unclear), Record(1, ComplianceResult.Unclear));

        var (_, body, _) = await Get(factory, "/summary");

        Assert.Equal(2, (int?)body["total"]);
        Assert.Equal(0, (int?)body["byResult"]!["COMPLIES"]);
        Assert.Equal(0, (int?)body["byResult"]!["DEVIATES"]);
        Assert.Equal(2, (int?)body["byResult"]!["UNCLEAR"]);
        Assert.Equal(3, body["byResult"]!.AsObject().Count);
    }
}
