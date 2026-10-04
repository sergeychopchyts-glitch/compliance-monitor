using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ComplianceMonitor.Client.Tests;

public sealed class ClientAppTests
{
    private const string ProblemJson = "application/problem+json";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Run(int ExitCode, string Output, string Error);

    private static async Task<Run> RunClient(
        FakeApi api, string[] args, Dictionary<string, string>? environment = null, CancellationToken? cancellationToken = null)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await ClientApp.RunAsync(
            args, name => environment?.GetValueOrDefault(name), api, output, error, cancellationToken ?? Ct);
        return new Run(exit, output.ToString(), error.ToString());
    }

    private static void AssertNoStackTrace(Run run)
    {
        Assert.DoesNotContain("   at ", run.Output + run.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", run.Output + run.Error, StringComparison.Ordinal);
    }

    // ---- usage ----

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("help")]
    public async Task Help_PrintsUsageWithExitCodes(string flag)
    {
        var api = FakeApi.Healthy();

        var run = await RunClient(api, [flag]);

        Assert.Equal(0, run.ExitCode);
        Assert.Empty(api.Requests);
        foreach (var command in new[] { "demo", "analyze", "run-cases", "history", "summary", "--base-url" })
        {
            Assert.Contains(command, run.Output, StringComparison.Ordinal);
        }

        foreach (var code in new[] { "0 ", "1 ", "2 ", "3 ", "64 ", "130 " })
        {
            Assert.Contains("\n  " + code, run.Output, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(new[] { "bogus" }, "Unknown command 'bogus'")]
    [InlineData(new[] { "analyze" }, "analyze needs --action")]
    [InlineData(new[] { "analyze", "--action", "a" }, "analyze needs --action")]
    [InlineData(new[] { "analyze", "--action", "a", "--guideline", " " }, "analyze needs --action")]
    [InlineData(new[] { "history", "--limit", "abc" }, "--limit must be a whole number")]
    [InlineData(new[] { "history", "--limit", "-1" }, "--limit must be a whole number")]
    [InlineData(new[] { "history", "--result", "MAYBE" }, "--result must be one of COMPLIES, DEVIATES, UNCLEAR")]
    [InlineData(new[] { "summary", "--limit", "5" }, "--limit is not an option of 'summary'")]
    [InlineData(new[] { "history", "--limit" }, "--limit needs a value")]
    [InlineData(new[] { "history", "--limit", "1", "--limit", "2" }, "--limit was given more than once")]
    [InlineData(new[] { "summary", "extra" }, "Unexpected argument 'extra'")]
    [InlineData(new[] { "--base-url", "not a url", "summary" }, "is not an http(s) URL")]
    [InlineData(new[] { "--base-url", "ftp://example.com", "summary" }, "is not an http(s) URL")]
    public async Task BadUsage_Exits64WithMessageAndNoRequest(string[] args, string message)
    {
        var api = FakeApi.Healthy();

        var run = await RunClient(api, args);

        Assert.Equal(64, run.ExitCode);
        Assert.Contains(message, run.Error, StringComparison.Ordinal);
        Assert.Empty(api.Requests);
    }

    [Fact]
    public async Task BadUsage_PrintsUsage()
    {
        var run = await RunClient(FakeApi.Healthy(), ["bogus"]);

        Assert.Contains("Usage:", run.Error, StringComparison.Ordinal);
    }

    // ---- base URL ----

    [Fact]
    public async Task BaseUrl_FlagWinsOverEnvironment()
    {
        var api = FakeApi.Healthy();

        await RunClient(api, ["summary", "--base-url", "http://flag:1234"], new() { ["COMPLIANCE_API_URL"] = "http://env:9999" });

        Assert.Equal(new Uri("http://flag:1234/summary"), Assert.Single(api.Requests).Uri);
    }

    [Fact]
    public async Task BaseUrl_FallsBackToEnvironment()
    {
        var api = FakeApi.Healthy();

        await RunClient(api, ["summary"], new() { ["COMPLIANCE_API_URL"] = "http://env:9999/base" });

        Assert.Equal(new Uri("http://env:9999/base/summary"), Assert.Single(api.Requests).Uri);
    }

    [Fact]
    public async Task BaseUrl_DefaultsToTheApiLaunchUrl()
    {
        var api = FakeApi.Healthy();

        await RunClient(api, ["summary"]);

        Assert.Equal(new Uri("http://localhost:5080/summary"), Assert.Single(api.Requests).Uri);
    }

    [Fact]
    public void BaseUrl_DefaultMatchesTheApiLaunchSettings()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "ComplianceMonitor.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        var settings = JsonNode.Parse(File.ReadAllText(
            Path.Combine(root.FullName, "src", "ComplianceMonitor.Api", "Properties", "launchSettings.json")))!;
        var applicationUrl = (string?)settings["profiles"]?["http"]?["applicationUrl"];

        Assert.Equal(ClientDefaults.BaseUrl, new Uri(applicationUrl!));
    }

    // ---- success output ----

    [Fact]
    public async Task Analyze_PostsActionAndGuidelineAndPrintsResult()
    {
        var api = FakeApi.Healthy();

        var run = await RunClient(api, ["analyze", "--action", "Closed ticket", "--guideline", "Send an email"]);

        Assert.Equal(0, run.ExitCode);
        var request = Assert.Single(api.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/analyze", request.Uri.AbsolutePath);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"action":"Closed ticket","guideline":"Send an email"}"""), JsonNode.Parse(request.Body!)));
        Assert.Contains("Result:      COMPLIES", run.Output, StringComparison.Ordinal);
        Assert.Contains("Confidence:  0.88", run.Output, StringComparison.Ordinal);
        Assert.Contains("Decided by:  MODEL (MODEL_CLASSIFICATION)", run.Output, StringComparison.Ordinal);
        Assert.Contains("Timestamp:   2026-10-03T10:15:00Z", run.Output, StringComparison.Ordinal);
        Assert.Empty(run.Error);
    }

    [Fact]
    public async Task RunCases_AllMatch_Exits0WithPassPerCase()
    {
        var api = FakeApi.Healthy(action => CommandRunner.BriefCases.Single(c => c.Action == action).Expected);

        var run = await RunClient(api, ["run-cases"]);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(CommandRunner.BriefCases.Select(c => c.Action), api.Requests.Select(r => JsonDocument.Parse(r.Body!).RootElement.GetProperty("action").GetString()));
        Assert.Equal(4, run.Output.Split('\n').Count(line => line.TrimEnd().EndsWith("PASS", StringComparison.Ordinal)));
        Assert.Contains("4/4 cases passed.", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunCases_OneMismatch_Exits1AndMarksItFail()
    {
        var api = FakeApi.Healthy(action => action.StartsWith("Rebooted", StringComparison.Ordinal)
            ? "COMPLIES"
            : CommandRunner.BriefCases.Single(c => c.Action == action).Expected);

        var run = await RunClient(api, ["run-cases"]);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("Case 3  expected DEVIATES  actual COMPLIES  (0.88, MODEL_CLASSIFICATION)  FAIL", run.Output, StringComparison.Ordinal);
        Assert.Contains("3/4 cases passed.", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task History_SendsLimitAndResultAndPrintsRows()
    {
        var api = FakeApi.Healthy(history: """
            [{"id":9,"action":"Closed ticket","guideline":"Send an email","result":"DEVIATES","confidence":0.97,"decisionSource":"MODEL","decisionReason":"MODEL_CLASSIFICATION","timestamp":"2026-10-03T10:16:00Z"},
             {"id":8,"action":"Rebooted","guideline":"Weekly","result":"DEVIATES","confidence":null,"decisionSource":"RULE","decisionReason":"MISSING_TEMPORAL_EVIDENCE","timestamp":"2026-10-03T10:15:00Z"}]
            """);

        var run = await RunClient(api, ["history", "--limit", "5", "--result", "deviates"]);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("/history?limit=5&result=DEVIATES", Assert.Single(api.Requests).Uri.PathAndQuery);
        Assert.Contains("#9    2026-10-03T10:16:00Z  DEVIATES 0.97  Closed ticket  |  Send an email", run.Output, StringComparison.Ordinal);
        Assert.Contains("#8    2026-10-03T10:15:00Z  DEVIATES —     Rebooted  |  Weekly", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task History_Empty_SaysSo()
    {
        var run = await RunClient(FakeApi.Healthy(), ["history"]);

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("No analyses yet.", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Summary_PrintsTotalsForAllThreeResults()
    {
        var run = await RunClient(FakeApi.Healthy(), ["summary"]);

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("Total     4", run.Output, StringComparison.Ordinal);
        Assert.Contains("COMPLIES  1", run.Output, StringComparison.Ordinal);
        Assert.Contains("DEVIATES  2", run.Output, StringComparison.Ordinal);
        Assert.Contains("UNCLEAR   1", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Demo_IsTheDefault_RunsCasesThenHistoryThenSummary()
    {
        var api = FakeApi.Healthy(action => CommandRunner.BriefCases.Single(c => c.Action == action).Expected);

        var run = await RunClient(api, []);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(
            ["/analyze", "/analyze", "/analyze", "/analyze", "/history", "/summary"],
            api.Requests.Select(r => r.Uri.AbsolutePath));
        Assert.True(run.Output.IndexOf("Brief cases", StringComparison.Ordinal) < run.Output.IndexOf("History", StringComparison.Ordinal));
        Assert.True(run.Output.IndexOf("History", StringComparison.Ordinal) < run.Output.IndexOf("Summary", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Demo_CaseFails_StillShowsHistoryAndSummaryAndExits1()
    {
        var api = FakeApi.Healthy(_ => "UNCLEAR");

        var run = await RunClient(api, ["demo"]);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("Summary", run.Output, StringComparison.Ordinal);
    }

    // ---- errors ----

    [Fact]
    public async Task ApiNotRunning_Exits2WithUrlAndHowToStartIt()
    {
        var api = FakeApi.Throwing(new HttpRequestException("Connection refused", null, HttpStatusCode.ServiceUnavailable));

        var run = await RunClient(api, [], new() { ["COMPLIANCE_API_URL"] = "http://localhost:5999" });

        Assert.Equal(2, run.ExitCode);
        Assert.Single(api.Requests); // demo stops at the first unreachable call
        Assert.Contains("Can't reach the API at http://localhost:5999/. Start it with: dotnet run --project src/ComplianceMonitor.Api", run.Error, StringComparison.Ordinal);
        AssertNoStackTrace(run);
    }

    [Fact]
    public async Task Timeout_Exits2AndSaysSoWithUrl()
    {
        var api = FakeApi.Throwing(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout", new TimeoutException()));

        var run = await RunClient(api, ["summary"]);

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("The API at http://localhost:5080/ did not respond within 60 s.", run.Error, StringComparison.Ordinal);
        AssertNoStackTrace(run);
    }

    [Fact]
    public async Task ValidationProblem_Exits3AndListsFieldErrors()
    {
        var api = FakeApi.Always(() => FakeApi.Json(HttpStatusCode.BadRequest, """
            {"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.","status":400,
             "errors":{"limit":["The limit must be between 1 and 200."],"result":["The result must be COMPLIES, DEVIATES or UNCLEAR."]}}
            """, ProblemJson));

        var run = await RunClient(api, ["history", "--limit", "500"]);

        Assert.Equal(3, run.ExitCode);
        Assert.Contains("The API rejected the request (400):", run.Error, StringComparison.Ordinal);
        Assert.Contains("  limit: The limit must be between 1 and 200.", run.Error, StringComparison.Ordinal);
        Assert.Contains("  result: The result must be COMPLIES, DEVIATES or UNCLEAR.", run.Error, StringComparison.Ordinal);
        AssertNoStackTrace(run);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway, "The classifier rejected the request.", "Hugging Face returned HTTP 401.")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "The classifier is temporarily unavailable.", "Hugging Face returned HTTP 503.")]
    [InlineData(HttpStatusCode.GatewayTimeout, "The classifier did not respond in time.", "Hugging Face did not respond within 30 s.")]
    public async Task ProblemDetails_Exits3WithTitleAndDetail(HttpStatusCode status, string title, string detail)
    {
        var api = FakeApi.Always(() => FakeApi.Json(status, JsonSerializer.Serialize(new { title, detail, status = (int)status }), ProblemJson));

        var run = await RunClient(api, ["analyze", "--action", "a", "--guideline", "g"]);

        Assert.Equal(3, run.ExitCode);
        Assert.Contains($"The API returned {(int)status}: {title}", run.Error, StringComparison.Ordinal);
        Assert.Contains($"  {detail}", run.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("Retry after", run.Error, StringComparison.Ordinal);
        AssertNoStackTrace(run);
    }

    [Fact]
    public async Task TooManyRequests_Exits3WithTheApiMessage()
    {
        var api = FakeApi.Always(() => FakeApi.Json(HttpStatusCode.TooManyRequests,
            """{"type":"urn:compliance-monitor:problem:too-many-analyses","title":"Too many analyses are running. Try again shortly.","status":429}""", ProblemJson));

        var run = await RunClient(api, ["analyze", "--action", "a", "--guideline", "g"]);

        Assert.Equal(3, run.ExitCode);
        Assert.Contains("The API returned 429: Too many analyses are running. Try again shortly.", run.Error, StringComparison.Ordinal);
        AssertNoStackTrace(run);
    }

    [Fact]
    public async Task ServiceUnavailableWithRetryAfter_ShowsIt()
    {
        var api = FakeApi.Always(() =>
        {
            var response = FakeApi.Json(HttpStatusCode.ServiceUnavailable, """{"title":"The classifier is temporarily unavailable.","detail":"Hugging Face returned HTTP 429.","status":503}""", ProblemJson);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(20));
            return response;
        });

        var run = await RunClient(api, ["analyze", "--action", "a", "--guideline", "g"]);

        Assert.Equal(3, run.ExitCode);
        Assert.Contains("  Retry after 20 s.", run.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "<html><body>Oops</body></html>", "text/html")]
    [InlineData(HttpStatusCode.NotFound, "", "text/plain")]
    [InlineData(HttpStatusCode.BadGateway, "[1,2,3]", "application/json")]
    [InlineData(HttpStatusCode.OK, "not json at all", "text/plain")]
    [InlineData(HttpStatusCode.OK, "null", "application/json")]
    public async Task UnreadableResponse_Exits3WithOneLineNamingTheStatus(HttpStatusCode status, string body, string mediaType)
    {
        var api = FakeApi.Always(() => FakeApi.Json(status, body, mediaType));

        var run = await RunClient(api, ["summary"]);

        Assert.Equal(3, run.ExitCode);
        Assert.Equal($"The API returned {(int)status} with a response the client could not read.", run.Error.Trim());
        AssertNoStackTrace(run);
    }

    [Fact]
    public async Task RunCases_ApiErrorOnOneCase_ReportsItRunsTheRestAndExits3()
    {
        var api = new FakeApi((request, body) => body!.Contains("Rebooted", StringComparison.Ordinal)
            ? FakeApi.Json(HttpStatusCode.ServiceUnavailable, """{"title":"The classifier is temporarily unavailable.","status":503}""", ProblemJson)
            : FakeApi.Healthy(action => CommandRunner.BriefCases.Single(c => c.Action == action).Expected)
                .Respond(request, body));

        var run = await RunClient(api, ["run-cases"]);

        Assert.Equal(3, run.ExitCode);
        Assert.Equal(4, api.Requests.Count);
        Assert.Contains("Case 3  expected DEVIATES  ERROR", run.Output, StringComparison.Ordinal);
        Assert.Contains("The API returned 503: The classifier is temporarily unavailable.", run.Error, StringComparison.Ordinal);
        Assert.Contains("3/4 cases passed.", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CtrlC_ExitsCleanlyWith130()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var api = new FakeApi((_, _) =>
        {
            cts.Cancel(); // the user presses Ctrl+C while the first request is in flight
            throw new OperationCanceledException(cts.Token);
        });

        var run = await RunClient(api, [], cancellationToken: cts.Token);

        Assert.Equal(130, run.ExitCode);
        Assert.Single(api.Requests);
        Assert.Equal("Cancelled.", run.Error.Trim());
        AssertNoStackTrace(run);
    }
}
