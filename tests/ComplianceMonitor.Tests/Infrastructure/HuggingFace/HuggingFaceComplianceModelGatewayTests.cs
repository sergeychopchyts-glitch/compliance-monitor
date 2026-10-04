using System.Net;
using System.Text.Json.Nodes;
using ComplianceMonitor.Application.Compliance.Models;
using ComplianceMonitor.Application.Errors;
using ComplianceMonitor.Infrastructure.Integrations.HuggingFace;
using ComplianceMonitor.Infrastructure.Integrations.HuggingFace.Strategies;
using ComplianceMonitor.Tests.TestSupport;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;

namespace ComplianceMonitor.Tests.Infrastructure.HuggingFace;

public sealed class HuggingFaceComplianceModelGatewayTests
{
    private const string Token = "hf_gateway_secret";
    private const string Complies = "complies with the guideline";
    private const string Deviates = "violates the guideline";
    private const string Unrelated = "is unrelated to the guideline";
    private const string Action = "Closed ticket #48219 and sent confirmation email";
    private const string Guideline = "All closed tickets must include a confirmation email";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HuggingFaceComplianceModelGateway CreateGateway(FakeHttpMessageHandler hf)
    {
        var options = new HuggingFaceOptions { ApiToken = Token };
        return new(TestClients.HuggingFace(hf, options), new ComplianceZeroShotPromptStrategy(), Options.Create(options));
    }

    private static FakeHttpMessageHandler Responding(params (string Label, double Score)[] scores) =>
        FakeHttpMessageHandler.Returning(
            HttpStatusCode.OK,
            new JsonArray([.. scores.Select(s => new JsonObject { ["label"] = s.Label, ["score"] = s.Score })]).ToJsonString());

    [Fact]
    public async Task EvaluateAsync_SendsTheProductionPrompt()
    {
        var hf = Responding((Complies, 0.9), (Deviates, 0.07), (Unrelated, 0.03));

        await CreateGateway(hf).EvaluateAsync(Action, Guideline, Ct);

        var expected = JsonNode.Parse("""
            {
              "inputs": "Action: Closed ticket #48219 and sent confirmation email. Guideline: All closed tickets must include a confirmation email",
              "parameters": {
                "candidate_labels": ["complies with the guideline", "violates the guideline", "is unrelated to the guideline"],
                "hypothesis_template": "This action {}.",
                "multi_label": false
              }
            }
            """);
        var body = JsonNode.Parse(Assert.Single(hf.Requests).Body!);
        Assert.True(JsonNode.DeepEquals(expected, body), body!.ToJsonString());
    }

    [Fact]
    public async Task EvaluateAsync_MapsLabelsByTextNotPosition_AndRecordsProvenance()
    {
        // Lowest first, the opposite of HF's sorted order, so index 0 is never the answer.
        var hf = Responding((Unrelated, 0.05), (Complies, 0.15), (Deviates, 0.8));

        var evaluation = await CreateGateway(hf).EvaluateAsync(Action, Guideline, Ct);

        Assert.Equal(new ModelScore(Deviates, ComplianceResult.Deviates, 0.8), evaluation.Top);
        Assert.Equal("HuggingFace", evaluation.Provider);
        Assert.Equal("facebook/bart-large-mnli", evaluation.ModelId);
        Assert.Equal("combined-three-label-v1", evaluation.Strategy);
        Assert.Equal(3, evaluation.Scores.Count);
    }

    public static TheoryData<string, string, string> BadLabelSets => new()
    {
        { Complies, Deviates, "unexpected label" },          // unknown
        { Complies, Complies, Deviates },                    // duplicate
        { Complies.ToUpperInvariant(), Deviates, Unrelated }, // differs only in case
    };

    [Theory]
    [MemberData(nameof(BadLabelSets))]
    public async Task EvaluateAsync_LabelsNotExactlyThoseSent_IsInvalidResponse(string a, string b, string c)
    {
        var ex = await Assert.ThrowsAsync<ModelGatewayException>(
            () => CreateGateway(Responding((a, 0.5), (b, 0.3), (c, 0.2))).EvaluateAsync(Action, Guideline, Ct));

        Assert.Equal(ModelGatewayFailureKind.InvalidResponse, ex.Kind);
    }

    [Fact]
    public async Task EvaluateAsync_CandidateLabelMissing_IsInvalidResponse()
    {
        var ex = await Assert.ThrowsAsync<ModelGatewayException>(
            () => CreateGateway(Responding((Complies, 0.6), (Deviates, 0.4))).EvaluateAsync(Action, Guideline, Ct));

        Assert.Equal(ModelGatewayFailureKind.InvalidResponse, ex.Kind);
    }

    public static TheoryData<FakeHttpMessageHandler, ModelGatewayFailureKind> Failures => new()
    {
        { FakeHttpMessageHandler.Returning(HttpStatusCode.ServiceUnavailable, Token), ModelGatewayFailureKind.Unavailable },
        { FakeHttpMessageHandler.Returning(HttpStatusCode.TooManyRequests, Token), ModelGatewayFailureKind.Unavailable },
        { FakeHttpMessageHandler.Throwing(new HttpRequestException("refused")), ModelGatewayFailureKind.Unavailable },
        { FakeHttpMessageHandler.Throwing(new BrokenCircuitException()), ModelGatewayFailureKind.Unavailable },
        { FakeHttpMessageHandler.Throwing(new TaskCanceledException("timeout", new TimeoutException())), ModelGatewayFailureKind.Timeout },
        { FakeHttpMessageHandler.Returning(HttpStatusCode.PaymentRequired, Token), ModelGatewayFailureKind.CreditsExhausted },
        { FakeHttpMessageHandler.Returning(HttpStatusCode.Unauthorized, Token), ModelGatewayFailureKind.Rejected },
        { FakeHttpMessageHandler.Returning(HttpStatusCode.BadRequest, Token), ModelGatewayFailureKind.Rejected },
        { FakeHttpMessageHandler.Returning(HttpStatusCode.OK, "not json"), ModelGatewayFailureKind.InvalidResponse },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task EvaluateAsync_ProviderFailures_BecomeProviderNeutralExceptions(FakeHttpMessageHandler hf, ModelGatewayFailureKind kind)
    {
        var ex = await Assert.ThrowsAsync<ModelGatewayException>(() => CreateGateway(hf).EvaluateAsync(Action, Guideline, Ct));

        Assert.Equal(kind, ex.Kind);
        Assert.DoesNotContain(Token, ex.Message, StringComparison.Ordinal); // HF answered with the token in its body
    }

    [Fact]
    public async Task EvaluateAsync_RetryAfterFromHf_IsKept()
    {
        var hf = new FakeHttpMessageHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(20));
            return response;
        });

        var ex = await Assert.ThrowsAsync<ModelGatewayException>(() => CreateGateway(hf).EvaluateAsync(Action, Guideline, Ct));

        Assert.Equal(TimeSpan.FromSeconds(20), ex.RetryAfter);
    }

    [Fact]
    public async Task EvaluateAsync_OverTheModelBudget_IsInputTooLongWithoutCallingHf()
    {
        var hf = Responding((Complies, 1.0), (Deviates, 0.0), (Unrelated, 0.0));

        var ex = await Assert.ThrowsAsync<ModelGatewayException>(
            () => CreateGateway(hf).EvaluateAsync(new string('a', 2000), Guideline, Ct));

        Assert.Equal(ModelGatewayFailureKind.InputTooLong, ex.Kind);
        Assert.Contains("reads 1024", ex.Message, StringComparison.Ordinal);
        Assert.Empty(hf.Requests);
    }
}
