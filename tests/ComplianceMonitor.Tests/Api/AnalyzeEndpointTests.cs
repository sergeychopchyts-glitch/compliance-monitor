using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ComplianceMonitor.Api.Errors;
using ComplianceMonitor.Application.Compliance.Models;
using ComplianceMonitor.Application.Errors;
using ComplianceMonitor.Infrastructure.Integrations.HuggingFace;
using ComplianceMonitor.Infrastructure.Integrations.HuggingFace.Strategies;
using ComplianceMonitor.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace ComplianceMonitor.Tests.Api;

/// <summary>The real API, service, policies and SQLite repository; only the model gateway is faked.</summary>
public sealed class AnalyzeEndpointTests
{
    private const string Action = "Closed ticket #48219 and sent confirmation email";
    private const string Guideline = "All closed tickets must include a confirmation email";
    private const string Weekly = "Servers must be rebooted weekly and logs reviewed after restart";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<HttpResponseMessage> PostJson(ApiFactory factory, string json) =>
        factory.CreateClient().PostAsync(
            new Uri("/analyze", UriKind.Relative), new StringContent(json, Encoding.UTF8, "application/json"), Ct);

    private static Task<HttpResponseMessage> Post(ApiFactory factory, object body) =>
        PostJson(factory, JsonSerializer.Serialize(body));

    private static async Task<JsonNode> ReadJson(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;

    [Fact]
    public async Task Analyze_Valid_ReturnsContractAndStoresTheAuditTrail()
    {
        using var factory = new ApiFactory();

        using var response = await Post(factory, new { action = $"  {Action} ", guideline = $"\t{Guideline}  " });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync(Ct);
        var expected = JsonNode.Parse($$"""
            {
              "id": 1,
              "action": "{{Action}}",
              "guideline": "{{Guideline}}",
              "result": "COMPLIES",
              "confidence": 0.94,
              "decisionSource": "MODEL",
              "decisionReason": "MODEL_CLASSIFICATION",
              "timestamp": "2026-10-03T10:15:00Z"
            }
            """);
        Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(raw)), raw);

        Assert.Equal([(Action, Guideline)], factory.Model.Calls);
        var row = Assert.Single(await factory.GetAnalysesAsync());
        Assert.Equal(new DateTime(2026, 10, 3, 10, 15, 0, DateTimeKind.Utc), row.CreatedAt);
        Assert.Equal("fake-provider", row.ModelProvider);
        Assert.Equal("fake-model", row.ModelId);
        Assert.Equal("fake-strategy", row.Strategy);
        Assert.Equal(ComplianceResult.Complies, row.ModelTopResult);
        Assert.Equal(0.94, row.ModelTopScore);
        Assert.Equal(0.5, row.ConfidenceThreshold);
        Assert.Contains("\"label\":\"complies\"", row.RawScoresJson, StringComparison.Ordinal);
    }

    public static TheoryData<string, string, ComplianceResult, double, string, double?, string, string> Decisions => new()
    {
        // action, guideline, model top, model score -> result, confidence, source, reason
        { Action, Guideline, ComplianceResult.Deviates, 0.8, "DEVIATES", 0.8, "MODEL", "MODEL_CLASSIFICATION" },
        { "Watered the plants", Guideline, ComplianceResult.Unclear, 0.7, "UNCLEAR", 0.7, "MODEL", "MODEL_CLASSIFICATION" },
        { Action, Guideline, ComplianceResult.Complies, 0.45, "UNCLEAR", null, "RULE", "INSUFFICIENT_MODEL_CONFIDENCE" },
        { "Rebooted the server and checked logs", Weekly, ComplianceResult.Complies, 0.88, "DEVIATES", null, "RULE", "MISSING_TEMPORAL_EVIDENCE" },
        { "Skipped torque confirmation at Station 3", "No guidelines exist for this case.", ComplianceResult.Complies, 0.9, "UNCLEAR", null, "RULE", "NO_APPLICABLE_GUIDELINE" },
    };

    [Theory]
    [MemberData(nameof(Decisions))]
    public async Task Analyze_EachDecisionPath_SerializesResultSourceAndReasonAsStrings(
        string action, string guideline, ComplianceResult modelTop, double modelScore,
        string result, double? confidence, string source, string reason)
    {
        using var factory = new ApiFactory();
        factory.Model.Evaluation = FakeModelGateway.Scores(modelTop, modelScore);

        using var response = await Post(factory, new { action, guideline });

        var body = await ReadJson(response);
        Assert.Equal(result, (string?)body["result"]);
        Assert.Equal(confidence, (double?)body["confidence"]); // null when a policy decided: no made-up confidence
        Assert.Equal(source, (string?)body["decisionSource"]);
        Assert.Equal(reason, (string?)body["decisionReason"]);
    }

    [Theory]
    [InlineData(0.8778579235076904, 0.88)]
    [InlineData(0.625, 0.63)]
    [InlineData(0.994, 0.99)]
    [InlineData(1.0, 1.0)]
    public async Task Analyze_RoundsConfidenceInResponseButStoresFullPrecision(double score, double expected)
    {
        using var factory = new ApiFactory();
        factory.Model.Evaluation = FakeModelGateway.Scores(ComplianceResult.Complies, score);

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
        Assert.Empty(factory.Model.Calls);
        Assert.Empty(await factory.GetAnalysesAsync());
    }

    [Fact]
    public async Task Analyze_FieldsAtMaxLength_AreAcceptedAndStoredIntact()
    {
        using var factory = new ApiFactory();
        var action = new string('a', 2000);
        var guideline = new string('g', 2000);

        using var response = await Post(factory, new { action, guideline });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var row = Assert.Single(await factory.GetAnalysesAsync());
        Assert.Equal(action, row.Action);
        Assert.Equal(guideline, row.Guideline);
    }

    [Fact]
    public async Task Analyze_MaxLengthIsCheckedBeforeTrimming()
    {
        // DataAnnotations validate the raw body; the service trims afterwards. Padding counts towards 2000.
        using var factory = new ApiFactory();

        using var response = await Post(factory, new { action = $" {new string('a', 2000)} ", guideline = "g" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
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
        var problem = await ReadJson(response);
        Assert.Equal(400, (int?)problem["status"]);
        Assert.False(string.IsNullOrEmpty((string?)problem["title"]));
        Assert.Empty(factory.Model.Calls);
        Assert.Empty(await factory.GetAnalysesAsync());
    }

    public static TheoryData<ModelGatewayFailureKind, int, string> Failures => new()
    {
        { ModelGatewayFailureKind.Timeout, 504, "classifier-timeout" },
        { ModelGatewayFailureKind.Unavailable, 503, "classifier-unavailable" },
        { ModelGatewayFailureKind.CreditsExhausted, 503, "classifier-credits-exhausted" },
        { ModelGatewayFailureKind.Rejected, 502, "classifier-rejected" },
        { ModelGatewayFailureKind.InvalidResponse, 502, "classifier-bad-response" },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task Analyze_ModelFails_ReturnsProblemDetailsAndStoresNothing(ModelGatewayFailureKind kind, int expectedStatus, string expectedType)
    {
        using var factory = new ApiFactory();
        factory.Model.Throws = new ModelGatewayException(kind, "The model provider returned HTTP 418.");

        using var response = await Post(factory, new { action = Action, guideline = Guideline });

        Assert.Equal(expectedStatus, (int)response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await ReadJson(response);
        Assert.Equal(expectedStatus, (int?)problem["status"]);
        Assert.Equal($"urn:compliance-monitor:problem:{expectedType}", (string?)problem["type"]);
        Assert.Equal(ModelGatewayExceptionHandler.Describe(kind).Title, (string?)problem["title"]);
        Assert.Equal("The model provider returned HTTP 418.", (string?)problem["detail"]);
        Assert.NotNull(problem["traceId"]);
        Assert.Empty(await factory.GetAnalysesAsync());
    }

    [Fact]
    public async Task Analyze_ModelUnavailableWithRetryAfter_PassesItThroughRoundedUp()
    {
        using var factory = new ApiFactory();
        factory.Model.Throws = new ModelGatewayException(ModelGatewayFailureKind.Unavailable, "busy", TimeSpan.FromSeconds(19.2));

        using var response = await Post(factory, new { action = Action, guideline = Guideline });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(20), response.Headers.RetryAfter?.Delta);
    }

    [Fact]
    public async Task Analyze_InputTooLongForTheModel_Returns400NamingBothFields()
    {
        using var factory = new ApiFactory();
        factory.Model.Throws = new ModelGatewayException(ModelGatewayFailureKind.InputTooLong, "Too long for the model.");

        using var response = await Post(factory, new { action = Action, guideline = Guideline });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ReadJson(response);
        Assert.Equal("Too long for the model.", (string?)problem["errors"]?["action"]?[0]);
        Assert.Equal("Too long for the model.", (string?)problem["errors"]?["guideline"]?[0]);
        Assert.Empty(await factory.GetAnalysesAsync());
    }

    [Fact]
    public async Task Analyze_UnexpectedException_Returns500ProblemAndStoresNothing()
    {
        using var factory = new ApiFactory();
        factory.Model.Throws = new InvalidOperationException("boom");

        using var response = await Post(factory, new { action = Action, guideline = Guideline });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var raw = await response.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain("boom", raw, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(InvalidOperationException), raw, StringComparison.Ordinal);
        Assert.DoesNotContain(" at ", raw, StringComparison.Ordinal); // no stack frames outside Development
        Assert.Empty(await factory.GetAnalysesAsync());
    }

    [Fact]
    public void Contracts_DoNotExposeProviderOrPersistenceTypes()
    {
        var contracts = typeof(ComplianceMonitor.Api.Features.Analyze.AnalysisResponse).Assembly.GetTypes()
            .Where(t => t.Namespace?.StartsWith("ComplianceMonitor.Api.Features", StringComparison.Ordinal) == true);

        var leaks = contracts
            .SelectMany(t => t.GetProperties().Select(p => (t, p.PropertyType)))
            .Where(x => x.PropertyType.Namespace?.StartsWith("ComplianceMonitor.Infrastructure", StringComparison.Ordinal) == true)
            .Select(x => $"{x.t.Name}: {x.PropertyType.Name}")
            .ToList();

        Assert.Empty(leaks);
    }
}

/// <summary>The real model gateway and HF client wired through DI; only Hugging Face's HTTP is faked.</summary>
public sealed class AnalyzeWiringTests
{
    private const string Complies = "complies with the guideline";
    private const string Deviates = "violates the guideline";
    private const string Unrelated = "is unrelated to the guideline";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<HttpResponseMessage> Post(ApiFactory factory, string action, string guideline) =>
        factory.CreateClient().PostAsJsonAsync(new Uri("/analyze", UriKind.Relative), new { action, guideline }, Ct);

    private static FakeHttpMessageHandler Hf(double complies, double deviates, double unrelated) =>
        FakeHttpMessageHandler.Returning(HttpStatusCode.OK,
            $$"""[{"label":"{{Unrelated}}","score":{{unrelated}}},{"label":"{{Deviates}}","score":{{deviates}}},{"label":"{{Complies}}","score":{{complies}}}]""");

    [Fact]
    public async Task Analyze_RealGateway_MapsHfResponseAndStoresProvenance()
    {
        var hf = Hf(0.8, 0.15, 0.05);
        using var factory = new ApiFactory { HuggingFaceHandler = hf };

        using var response = await Post(factory, "Closed ticket and sent email", "Closed tickets need an email");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ApiFactory.FakeToken, Assert.Single(hf.Requests).Authorization?.Parameter);
        var row = Assert.Single(await factory.GetAnalysesAsync());
        Assert.Equal(ComplianceResult.Complies, row.Result);
        Assert.Equal("HuggingFace", row.ModelProvider);
        Assert.Equal("facebook/bart-large-mnli", row.ModelId);
        Assert.Equal(new ComplianceZeroShotStrategy().Name, row.Strategy);
    }

    [Fact]
    public async Task Analyze_BriefCase3_ModelSaysCompliesButTheTemporalPolicyDeviates()
    {
        using var factory = new ApiFactory { HuggingFaceHandler = Hf(0.88, 0.07, 0.05) };

        using var response = await Post(factory, "Rebooted the server and checked logs", "Servers must be rebooted weekly and logs reviewed after restart");

        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;
        Assert.Equal("DEVIATES", (string?)body["result"]);
        Assert.Equal("MISSING_TEMPORAL_EVIDENCE", (string?)body["decisionReason"]);
        var row = Assert.Single(await factory.GetAnalysesAsync());
        Assert.Equal(ComplianceResult.Complies, row.ModelTopResult); // what the model said stays on record
        Assert.Equal(0.88, row.ModelTopScore);
    }

    [Fact]
    public async Task Analyze_RealGatewayHfRejectsToken_Returns502WithoutLeakingToken()
    {
        var hf = FakeHttpMessageHandler.Returning(HttpStatusCode.Unauthorized, $$"""{"error":"Invalid token {{ApiFactory.FakeToken}}"}""");
        using var factory = new ApiFactory { HuggingFaceHandler = hf };

        using var response = await Post(factory, "a", "b");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.DoesNotContain(ApiFactory.FakeToken, await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Empty(await factory.GetAnalysesAsync());
        Assert.Contains(factory.Logs.Entries, e => e.Category == typeof(ModelGatewayExceptionHandler).FullName);
        Assert.All(factory.Logs.Entries, e =>
        {
            Assert.DoesNotContain(ApiFactory.FakeToken, e.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("Bearer", e.Message, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task Analyze_HfNeverAnswers_Returns504AfterTotalTimeout()
    {
        // HF hangs on every attempt; the pipeline's attempt and total timeouts run on the factory's fake clock.
        var hf = new FakeHttpMessageHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        });
        using var factory = new ApiFactory { HuggingFaceHandler = hf };
        using var client = factory.CreateClient(); // start the host before the clock moves

        // Event-driven, so slow continuations on a cold machine can't let the clock overtake the pipeline.
        var pending = client.PostAsJsonAsync(new Uri("/analyze", UriKind.Relative), new { action = "a", guideline = "b" }, Ct);
        await AdvanceUntil(factory, () => hf.Requests.Count == 1, TimeSpan.Zero);
        factory.Time.Advance(TimeSpan.FromSeconds(10)); // the first attempt's timeout
        await AdvanceUntil(factory, () => hf.Requests.Count == 2, TimeSpan.FromMilliseconds(250)); // backoff
        await AdvanceUntil(factory, () => pending.IsCompleted, TimeSpan.FromSeconds(1)); // up to the 30 s total

        using var response = await pending;
        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.True(hf.Requests.Count > 1, $"Expected the attempt timeout to trigger retries; saw {hf.Requests.Count} attempt(s).");
        Assert.Empty(await factory.GetAnalysesAsync());
    }

    private static async Task AdvanceUntil(ApiFactory factory, Func<bool> done, TimeSpan step)
    {
        for (var i = 0; i < 500 && !done(); i++)
        {
            factory.Time.Advance(step);
            await Task.Delay(10, Ct);
        }

        Assert.True(done(), "The pipeline did not reach the expected state.");
    }

    [Fact]
    public async Task Analyze_TooLongForTheModel_Returns400NamingBothFieldsWithoutCallingHf()
    {
        var hf = Hf(1, 0, 0);
        using var factory = new ApiFactory { HuggingFaceHandler = hf };

        using var response = await Post(factory, new string('a', 1500), "Closed tickets need an email");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;
        var message = (string?)problem["errors"]?["action"]?[0];
        Assert.Contains("reads 1024", message, StringComparison.Ordinal);
        Assert.Equal(message, (string?)problem["errors"]?["guideline"]?[0]);
        Assert.Empty(hf.Requests);
        Assert.Empty(await factory.GetAnalysesAsync());
    }

    [Fact]
    public async Task Analyze_LongButWithinTheModelBudget_IsClassified()
    {
        // Production prompt: "Action: " + a + ". Guideline: " + g (21 bytes), the longest hypothesis
        // "This action is unrelated to the guideline." (42) and 4 special tokens: 67 + a + g <= 1024.
        var hf = Hf(0.8, 0.1, 0.1);
        using var factory = new ApiFactory { HuggingFaceHandler = hf };

        using var response = await Post(factory, new string('a', 900), new string('g', 57)); // exactly 1024

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(hf.Requests);
    }

    [Fact]
    public async Task Analyze_NoGuideline_DecidesByRuleWithoutCallingHf()
    {
        var hf = Hf(1, 0, 0);
        using var factory = new ApiFactory { HuggingFaceHandler = hf };

        using var response = await Post(factory, new string('a', 2000), "No guidelines exist for this case."); // too long for the model, but it isn't asked

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(hf.Requests);
        var row = Assert.Single(await factory.GetAnalysesAsync());
        Assert.Equal(ComplianceResult.Unclear, row.Result);
        Assert.Equal(DecisionReason.NoApplicableGuideline, row.DecisionReason);
        Assert.Null(row.RawScoresJson);
    }

    [Fact]
    public async Task NonLiveFactory_BlocksTheNetwork()
    {
        using var factory = new ApiFactory();
        using var scope = factory.Services.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<HuggingFaceZeroShotClient>();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ClassifyAsync(new ComplianceZeroShotStrategy().Build("a", "b").ToRequest(), Ct));

        Assert.Equal(NoNetworkHandler.Message, ex.Message);
    }
}
