using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ComplianceMonitor.Api.Classification;
using ComplianceMonitor.Tests.Infrastructure;

namespace ComplianceMonitor.Tests.Endpoints;

public sealed class AnalyzeEndpointTests
{
    private const string Action = "Closed ticket #48219 and sent confirmation email";
    private const string Guideline = "All closed tickets must include a confirmation email";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<HttpResponseMessage> PostJson(ApiFactory factory, string json)
    {
        var client = factory.CreateClient();
        return client.PostAsync(
            new Uri("/analyze", UriKind.Relative), new StringContent(json, Encoding.UTF8, "application/json"), Ct);
    }

    private static Task<HttpResponseMessage> Post(ApiFactory factory, object body) =>
        PostJson(factory, JsonSerializer.Serialize(body));

    private static async Task<JsonNode> ReadJson(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;

    [Fact]
    public async Task Analyze_Valid_ReturnsContractAndStoresRow()
    {
        using var factory = new ApiFactory();

        using var response = await Post(factory, new { action = $"  {Action} ", guideline = Guideline });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync(Ct);
        Assert.Contains("\"result\":\"COMPLIES\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"timestamp\":\"2026-10-03T10:15:00Z\"", raw, StringComparison.Ordinal);
        var expected = JsonNode.Parse($$"""
            {
              "id": 1,
              "action": "{{Action}}",
              "guideline": "{{Guideline}}",
              "result": "COMPLIES",
              "confidence": 0.94,
              "decidedBy": "MODEL",
              "timestamp": "2026-10-03T10:15:00Z"
            }
            """);
        var body = JsonNode.Parse(raw)!;
        Assert.True(JsonNode.DeepEquals(expected, body), raw);

        Assert.Equal([(Action, Guideline)], factory.Classifier.Calls);
        var row = Assert.Single(await factory.GetAnalysesAsync());
        Assert.Equal(Action, row.Action);
        Assert.Equal(Guideline, row.Guideline);
        Assert.Equal(ComplianceResult.Complies, row.Result);
        Assert.Equal(0.94, row.Confidence);
        Assert.Equal(new DateTime(2026, 10, 3, 10, 15, 0, DateTimeKind.Utc), row.CreatedAt);
        Assert.Equal(DateTimeKind.Utc, row.CreatedAt.Kind);
        Assert.Equal("fake", row.Strategy);
        Assert.Equal(DecisionSource.Model, row.DecidedBy);
        Assert.Equal("""[{"label":"complies","score":0.94},{"label":"violates","score":0.06}]""", row.ScoresJson);
    }

    [Theory]
    [InlineData(ComplianceResult.Deviates, DecisionSource.Model, "DEVIATES", "MODEL")]
    [InlineData(ComplianceResult.Unclear, DecisionSource.LowConfidence, "UNCLEAR", "LOW_CONFIDENCE")]
    [InlineData(ComplianceResult.Unclear, DecisionSource.Rule, "UNCLEAR", "RULE")]
    public async Task Analyze_EachResult_SerializesAsUppercaseStrings(
        ComplianceResult result, DecisionSource decidedBy, string expectedResult, string expectedDecidedBy)
    {
        using var factory = new ApiFactory();
        factory.Classifier.Outcome = new ClassificationOutcome(result, 0.7, decidedBy, "fake", []);

        using var response = await Post(factory, new { action = Action, guideline = Guideline });

        var raw = await response.Content.ReadAsStringAsync(Ct);
        Assert.Contains($"\"result\":\"{expectedResult}\"", raw, StringComparison.Ordinal);
        Assert.Contains($"\"decidedBy\":\"{expectedDecidedBy}\"", raw, StringComparison.Ordinal);
        Assert.Equal(decidedBy, Assert.Single(await factory.GetAnalysesAsync()).DecidedBy);
    }

    [Theory]
    [InlineData(0.8778579235076904, 0.88)]
    [InlineData(0.125, 0.13)]
    [InlineData(0.994, 0.99)]
    [InlineData(1.0, 1.0)]
    public async Task Analyze_RoundsConfidenceInResponseButStoresFullPrecision(double score, double expected)
    {
        using var factory = new ApiFactory();
        factory.Classifier.Outcome = new ClassificationOutcome(ComplianceResult.Complies, score, DecisionSource.Model, "fake", []);

        using var response = await Post(factory, new { action = Action, guideline = Guideline });

        Assert.Equal(expected, (double?)(await ReadJson(response))["confidence"]);
        Assert.Equal(score, Assert.Single(await factory.GetAnalysesAsync()).Confidence);
    }

    public static TheoryData<string, string> InvalidBodies => new()
    {
        { """{"guideline":"g"}""", "action" },
        { """{"action":"a"}""", "guideline" },
        { """{"action":"   ","guideline":"g"}""", "action" },
        { """{"action":"a","guideline":""}""", "guideline" },
        { """{"action":null,"guideline":"g"}""", "action" },
        { $$"""{"action":"{{new string('a', 2001)}}","guideline":"g"}""", "action" },
        { $$"""{"action":"a","guideline":"{{new string('g', 2001)}}"}""", "guideline" },
    };

    [Theory]
    [MemberData(nameof(InvalidBodies))]
    public async Task Analyze_InvalidField_Returns400ValidationProblemWithoutClassifyingOrStoring(string json, string field)
    {
        using var factory = new ApiFactory();

        using var response = await PostJson(factory, json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await ReadJson(response);
        Assert.Equal(400, (int?)problem["status"]);
        Assert.NotNull(problem["errors"]?[field]);
        Assert.Empty(factory.Classifier.Calls);
        Assert.Empty(await factory.GetAnalysesAsync());
    }

    [Fact]
    public async Task Analyze_FieldsAtMaxLength_AreAccepted()
    {
        using var factory = new ApiFactory();

        using var response = await Post(factory, new { action = new string('a', 2000), guideline = new string('g', 2000) });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("""{"action":123,"guideline":"g"}""")]
    [InlineData("")]
    public async Task Analyze_UnreadableBody_Returns400Problem(string json)
    {
        using var factory = new ApiFactory();

        using var response = await PostJson(factory, json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Empty(factory.Classifier.Calls);
    }

    public static TheoryData<HuggingFaceException, int, string> ClassifierFailures => new()
    {
        { HuggingFaceTransientException.Timeout(30, null), 504, "classifier-timeout" },
        { HuggingFaceTransientException.FromStatus(429, null), 503, "classifier-unavailable" },
        { HuggingFaceTransientException.FromStatus(503, null), 503, "classifier-unavailable" },
        { HuggingFaceTransientException.FromStatus(500, null), 503, "classifier-unavailable" },
        { HuggingFaceTransientException.Unreachable(new HttpRequestException("refused")), 503, "classifier-unavailable" },
        { HuggingFacePermanentException.FromStatus(402), 503, "classifier-credits-exhausted" },
        { HuggingFacePermanentException.FromStatus(400), 502, "classifier-rejected" },
        { HuggingFacePermanentException.FromStatus(401), 502, "classifier-rejected" },
        { HuggingFacePermanentException.FromStatus(403), 502, "classifier-rejected" },
        { HuggingFacePermanentException.Malformed("no labels returned"), 502, "classifier-bad-response" },
    };

    [Theory]
    [MemberData(nameof(ClassifierFailures))]
    public async Task Analyze_ClassifierFails_ReturnsProblemDetailsAndStoresNothing(
        HuggingFaceException failure, int expectedStatus, string expectedType)
    {
        using var factory = new ApiFactory();
        factory.Classifier.Throws = failure;

        using var response = await Post(factory, new { action = Action, guideline = Guideline });

        Assert.Equal(expectedStatus, (int)response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await ReadJson(response);
        Assert.Equal(expectedStatus, (int?)problem["status"]);
        Assert.Equal($"urn:compliance-monitor:problem:{expectedType}", (string?)problem["type"]);
        Assert.Equal(failure.Message, (string?)problem["detail"]);
        Assert.NotNull(problem["traceId"]);
        Assert.Empty(await factory.GetAnalysesAsync());
    }

    [Fact]
    public async Task Analyze_RateLimitedWithRetryAfter_PassesRetryAfterThrough()
    {
        using var factory = new ApiFactory();
        factory.Classifier.Throws = HuggingFaceTransientException.FromStatus(429, TimeSpan.FromSeconds(19.2));

        using var response = await Post(factory, new { action = Action, guideline = Guideline });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(20), response.Headers.RetryAfter?.Delta);
    }

    [Fact]
    public async Task Analyze_UnexpectedException_Returns500ProblemAndStoresNothing()
    {
        using var factory = new ApiFactory();
        factory.Classifier.Throws = new InvalidOperationException("boom");

        using var response = await Post(factory, new { action = Action, guideline = Guideline });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("boom", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Empty(await factory.GetAnalysesAsync());
    }
}

/// <summary>The real classifier and HF client wired through DI; only HF's HTTP is faked.</summary>
public sealed class AnalyzeWiringTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<HttpResponseMessage> Post(ApiFactory factory, string action, string guideline) =>
        factory.CreateClient().PostAsJsonAsync(new Uri("/analyze", UriKind.Relative), new { action, guideline }, Ct);

    [Fact]
    public async Task Analyze_RealClassifier_MapsHfResponseAndStoresAuditFields()
    {
        var hf = FakeHttpMessageHandler.Returning(HttpStatusCode.OK,
            """[{"label":"violates the guideline","score":0.2},{"label":"complies with the guideline","score":0.8}]""");
        using var factory = new ApiFactory { HuggingFaceHandler = hf };

        using var response = await Post(factory, "Closed ticket and sent email", "Closed tickets need an email");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sent = Assert.Single(hf.Requests);
        Assert.Equal(ApiFactory.FakeToken, sent.Authorization?.Parameter);
        var row = Assert.Single(await factory.GetAnalysesAsync());
        Assert.Equal(ComplianceResult.Complies, row.Result);
        Assert.Equal(0.8, row.Confidence);
        Assert.Equal("placeholder", row.Strategy);
        Assert.Equal(DecisionSource.Model, row.DecidedBy);
        Assert.Contains("complies with the guideline", row.ScoresJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Analyze_RealClassifierHfRejectsToken_Returns502WithoutLeakingToken()
    {
        var hf = FakeHttpMessageHandler.Returning(HttpStatusCode.Unauthorized, $$"""{"error":"Invalid token {{ApiFactory.FakeToken}}"}""");
        using var factory = new ApiFactory { HuggingFaceHandler = hf };

        using var response = await Post(factory, "a", "b");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.DoesNotContain(ApiFactory.FakeToken, await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Empty(await factory.GetAnalysesAsync());
    }

    [Fact]
    public async Task Analyze_RealClassifierNoGuideline_DecidesByRuleWithoutCallingHf()
    {
        var hf = FakeHttpMessageHandler.Returning(HttpStatusCode.OK, "[]");
        using var factory = new ApiFactory { HuggingFaceHandler = hf };

        using var response = await Post(factory, "Skipped torque confirmation at Station 3", "No guidelines exist for this case.");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(hf.Requests);
        var row = Assert.Single(await factory.GetAnalysesAsync());
        Assert.Equal(ComplianceResult.Unclear, row.Result);
        Assert.Equal(DecisionSource.Rule, row.DecidedBy);
        Assert.Equal("[]", row.ScoresJson);
    }
}
