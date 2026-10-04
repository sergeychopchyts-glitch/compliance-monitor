using System.Net;
using System.Net.Http.Headers;
using ComplianceMonitor.Api.Classification;
using ComplianceMonitor.Api.Classification.Strategies;
using ComplianceMonitor.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Polly;

namespace ComplianceMonitor.Tests.Classification;

/// <summary>
/// The real DI registration (resilience pipeline, attempt counter, logging) over a fake primary handler.
/// Backoff is overridden to zero so the suite stays fast; Retry-After and timeouts run on a fake clock,
/// which tests advance explicitly, so nothing waits in real time.
/// </summary>
public sealed class HuggingFaceResilienceTests : IDisposable
{
    private const string Token = "hf_resilience_secret";
    private const string Ok = """[{"label":"complies with the guideline","score":0.9},{"label":"violates the guideline","score":0.1}]""";

    private static readonly ZeroShotRequest Request = new PlaceholderLabelStrategy().Build("action", "guideline").ToRequest();

    private readonly CapturingLoggerProvider _logs = new();
    private readonly FakeTimeProvider _time = new();
    private ServiceProvider? _provider;

    public void Dispose() => _provider?.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HuggingFaceZeroShotClient CreateClient(
        FakeHttpMessageHandler hf, Dictionary<string, string?>? settings = null, Action<HttpStandardResilienceOptions>? pipeline = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["HuggingFace:ApiToken"] = Token })
            .AddInMemoryCollection(settings ?? [])
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(_logs));
        services.AddSingleton<TimeProvider>(_time);
        services.AddComplianceClassification(configuration);
        services.AddHttpClient<HuggingFaceZeroShotClient>().ConfigurePrimaryHttpMessageHandler(() => hf);
        services.PostConfigureAll<HttpStandardResilienceOptions>(options =>
        {
            options.Retry.Delay = TimeSpan.Zero;
            pipeline?.Invoke(options);
        });

        _provider = services.BuildServiceProvider();
        return _provider.GetRequiredService<HuggingFaceZeroShotClient>();
    }

    private static FakeHttpMessageHandler Sequence(params Func<HttpResponseMessage>[] responses)
    {
        var next = 0;
        return new FakeHttpMessageHandler(_ => responses[Math.Min(next++, responses.Length - 1)]());
    }

    private static Func<HttpResponseMessage> Status(HttpStatusCode status, string body = "") =>
        () => new HttpResponseMessage(status) { Content = new StringContent(body) };

    /// <summary>
    /// Advances the fake clock in small steps until the call finishes (bounded, so a bug can't hang the suite).
    /// Async continuations run in real time while the clock keeps moving, so measured gaps can overshoot by a
    /// few steps: assert the lower bound exactly and keep upper bounds well below the next mechanism.
    /// </summary>
    private async Task<T> Advancing<T>(Task<T> call, TimeSpan step)
    {
        for (var i = 0; i < 2000 && !call.IsCompleted; i++)
        {
            _time.Advance(step);
            await Task.Delay(1, Ct);
        }

        return await call.WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    private LogEntry CallLog() => Assert.Single(_logs.Entries, e => e.Category == typeof(HuggingFaceZeroShotClient).FullName);

    [Fact]
    public async Task TwoServiceUnavailableThenOk_SucceedsOnThirdAttempt()
    {
        var hf = Sequence(Status(HttpStatusCode.ServiceUnavailable), Status(HttpStatusCode.ServiceUnavailable), Status(HttpStatusCode.OK, Ok));

        var scores = await CreateClient(hf).ClassifyAsync(Request, Ct);

        Assert.Equal(2, scores.Count);
        Assert.Equal(3, hf.Requests.Count);
        var log = CallLog();
        Assert.Equal(LogLevel.Information, log.Level);
        Assert.Equal("succeeded", log.State["Outcome"]);
        Assert.Equal(200, log.State["StatusCode"]);
        Assert.Equal(3, log.State["Attempts"]);
        Assert.True(log.State.ContainsKey("ElapsedMs"));
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task TransientStatus_IsRetried(HttpStatusCode status)
    {
        var hf = Sequence(Status(status), Status(HttpStatusCode.OK, Ok));

        await CreateClient(hf).ClassifyAsync(Request, Ct);

        Assert.Equal(2, hf.Requests.Count);
    }

    [Fact]
    public async Task NetworkFailure_IsRetried()
    {
        var calls = 0;
        var hf = new FakeHttpMessageHandler(_ => ++calls == 1
            ? throw new HttpRequestException("Connection reset")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Ok) });

        await CreateClient(hf).ClassifyAsync(Request, Ct);

        Assert.Equal(2, hf.Requests.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.PaymentRequired)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task PermanentStatus_IsCalledExactlyOnce(HttpStatusCode status)
    {
        var hf = Sequence(Status(status), Status(HttpStatusCode.OK, Ok));

        var ex = await Assert.ThrowsAsync<HuggingFacePermanentException>(() => CreateClient(hf).ClassifyAsync(Request, Ct));

        Assert.Single(hf.Requests);
        Assert.Equal((int)status, ex.UpstreamStatusCode);
        var log = CallLog();
        Assert.Equal(LogLevel.Warning, log.Level);
        Assert.Equal("failed (permanent)", log.State["Outcome"]);
        Assert.Equal(1, log.State["Attempts"]);
    }

    [Fact]
    public async Task TooManyRequestsWithRetryAfter_WaitsThatLongThenRetries()
    {
        var arrivals = new List<DateTimeOffset>();
        var hf = new FakeHttpMessageHandler(_ =>
        {
            arrivals.Add(_time.GetUtcNow());
            if (arrivals.Count > 1)
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Ok) };
            }

            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
            return response;
        });

        await Advancing(CreateClient(hf).ClassifyAsync(Request, Ct), TimeSpan.FromMilliseconds(100));

        Assert.Equal(2, arrivals.Count);
        // Backoff is zero in these tests, so the gap comes from Retry-After: at least 7 s, and far below
        // the 30 s total timeout.
        Assert.InRange(arrivals[1] - arrivals[0], TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task SlowAttempt_IsCutByAttemptTimeoutAndRetried()
    {
        var arrivals = new List<DateTimeOffset>();
        var hf = new FakeHttpMessageHandler(async (_, ct) =>
        {
            arrivals.Add(_time.GetUtcNow());
            if (arrivals.Count == 1)
            {
                await Task.Delay(Timeout.Infinite, ct); // hangs until the attempt timeout cancels it
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Ok) };
        });
        var client = CreateClient(hf, new() { ["HuggingFace:AttemptTimeoutSeconds"] = "2", ["HuggingFace:TimeoutSeconds"] = "10" });

        var scores = await Advancing(client.ClassifyAsync(Request, Ct), TimeSpan.FromMilliseconds(100));

        Assert.Equal(2, scores.Count);
        Assert.Equal(2, arrivals.Count);
        // Cut at the 2 s attempt timeout, not the 10 s total timeout.
        Assert.InRange(arrivals[1] - arrivals[0], TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5));
        Assert.Equal(2, CallLog().State["Attempts"]);
    }

    [Fact]
    public async Task CallLog_ReportsLatencyFromTheTimeProvider()
    {
        var hf = new FakeHttpMessageHandler(_ =>
        {
            _time.Advance(TimeSpan.FromMilliseconds(250));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Ok) };
        });

        await CreateClient(hf).ClassifyAsync(Request, Ct);

        Assert.Equal(250L, CallLog().State["ElapsedMs"]);
    }

    [Fact]
    public async Task RetriesExhausted_SurfaceAsTransientException()
    {
        var hf = Sequence(Status(HttpStatusCode.ServiceUnavailable));

        var ex = await Assert.ThrowsAsync<HuggingFaceTransientException>(() => CreateClient(hf).ClassifyAsync(Request, Ct));

        Assert.Equal(4, hf.Requests.Count); // the first attempt plus 3 retries
        Assert.Equal(503, ex.UpstreamStatusCode);
        var log = CallLog();
        Assert.Equal("failed (transient)", log.State["Outcome"]);
        Assert.Equal(4, log.State["Attempts"]);
    }

    [Fact]
    public async Task CircuitBreakerOpens_CallerGetsTransientAndHfIsNoLongerCalled()
    {
        var hf = Sequence(Status(HttpStatusCode.ServiceUnavailable));
        // Production needs 100 calls in the window before the breaker can open; two make the test practical.
        var client = CreateClient(hf, pipeline: options =>
        {
            options.CircuitBreaker.MinimumThroughput = 2;
            options.CircuitBreaker.FailureRatio = 0.5;
        });

        var first = await Assert.ThrowsAsync<HuggingFaceTransientException>(() => client.ClassifyAsync(Request, Ct));
        var callsBeforeSecond = hf.Requests.Count;
        var second = await Assert.ThrowsAsync<HuggingFaceTransientException>(() => client.ClassifyAsync(Request, Ct));

        Assert.Contains("circuit breaker open", first.Message + second.Message, StringComparison.Ordinal);
        Assert.Equal("Calls to Hugging Face are temporarily suspended (circuit breaker open).", second.Message);
        Assert.Equal(callsBeforeSecond, hf.Requests.Count); // open breaker: no request reached HF
    }

    [Fact]
    public async Task CallerCancels_StopsWithoutRetrying()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var hf = new FakeHttpMessageHandler(_ =>
        {
            cts.Cancel();
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateClient(hf).ClassifyAsync(Request, cts.Token));

        Assert.Single(hf.Requests);
        Assert.Equal("cancelled by caller", CallLog().State["Outcome"]);
    }

    [Fact]
    public async Task Logs_NeverContainTokenOrAuthorizationHeader()
    {
        var hf = Sequence(Status(HttpStatusCode.ServiceUnavailable, Token), Status(HttpStatusCode.Unauthorized, $"bad token {Token}"));

        await Assert.ThrowsAsync<HuggingFacePermanentException>(() => CreateClient(hf).ClassifyAsync(Request, Ct));

        Assert.NotEmpty(_logs.Entries);
        Assert.All(_logs.Entries, entry =>
        {
            var text = entry.Message + string.Join(' ', entry.State.Values);
            Assert.DoesNotContain(Token, text, StringComparison.Ordinal);
            Assert.DoesNotContain("Bearer", text, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void Timeouts_ComeFromConfigAndHttpClientTimeoutSitsAbovePipeline()
    {
        CreateClient(Sequence(Status(HttpStatusCode.OK, Ok)), new()
        {
            ["HuggingFace:TimeoutSeconds"] = "40",
            ["HuggingFace:AttemptTimeoutSeconds"] = "12",
        });

        var pipeline = _provider!.GetRequiredService<IOptionsMonitor<HttpStandardResilienceOptions>>()
            .Get($"{nameof(HuggingFaceZeroShotClient)}-standard");
        var httpClient = _provider!.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(HuggingFaceZeroShotClient));

        Assert.Equal(TimeSpan.FromSeconds(40), pipeline.TotalRequestTimeout.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(12), pipeline.AttemptTimeout.Timeout);
        Assert.Equal(3, pipeline.Retry.MaxRetryAttempts);
        Assert.Equal(DelayBackoffType.Exponential, pipeline.Retry.BackoffType);
        Assert.True(pipeline.Retry.UseJitter);
        Assert.True(pipeline.Retry.ShouldRetryAfterHeader);
        Assert.Equal(TimeSpan.FromSeconds(40) + ClassificationServiceCollectionExtensions.HttpClientTimeoutSlack, httpClient.Timeout);
    }

    [Fact]
    public void Timeouts_DefaultTo10SecondsPerAttemptAnd30Total()
    {
        CreateClient(Sequence(Status(HttpStatusCode.OK, Ok)));

        var pipeline = _provider!.GetRequiredService<IOptionsMonitor<HttpStandardResilienceOptions>>()
            .Get($"{nameof(HuggingFaceZeroShotClient)}-standard");

        Assert.Equal(TimeSpan.FromSeconds(30), pipeline.TotalRequestTimeout.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(10), pipeline.AttemptTimeout.Timeout);
    }
}
