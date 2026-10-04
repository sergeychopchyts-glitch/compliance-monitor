using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

namespace ComplianceMonitor.Infrastructure.Integrations.HuggingFace;

/// <summary>The typed Hugging Face client and its resilience pipeline.</summary>
public static class HuggingFaceResilience
{
    /// <summary>
    /// Slack between the pipeline's total timeout and HttpClient.Timeout. HttpClient.Timeout is only a
    /// backstop: it must never fire before the pipeline's own timeouts, or a slow call would surface as a
    /// bare cancellation instead of going through retry and timeout handling.
    /// </summary>
    public static readonly TimeSpan HttpClientTimeoutSlack = TimeSpan.FromSeconds(10);

    public static IServiceCollection AddHuggingFaceClient(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<HuggingFaceOptions>()
            .Bind(configuration.GetSection(HuggingFaceOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<HuggingFaceOptions>, HuggingFaceOptionsValidator>();
        services.AddTransient<AttemptCountingHandler>();

        var http = services.AddHttpClient<HuggingFaceZeroShotClient>();

        // Standard pipeline, outermost first: rate limiter, total timeout, retry, circuit breaker, attempt timeout.
        // Retry defaults: 3 retries, exponential backoff with jitter, honours Retry-After, retries 408, 429, 5xx,
        // network errors and attempt timeouts; never 400, 401, 402 or 403.
        // Retrying the POST is safe: zero-shot inference is idempotent (same input, same scores, no server-side
        // state), and nothing is stored until a classification has succeeded.
        var resilience = http.AddStandardResilienceHandler();

        // The resilience package sets HttpClient.Timeout to infinite; this runs after it and puts the
        // backstop back, above the pipeline's total timeout so it never fires first.
        http.ConfigureHttpClient((provider, client) =>
            client.Timeout = TimeSpan.FromSeconds(
                provider.GetRequiredService<IOptions<HuggingFaceOptions>>().Value.TimeoutSeconds) + HttpClientTimeoutSlack);

        // Added after the pipeline, so it runs inside it: once per attempt.
        http.AddHttpMessageHandler<AttemptCountingHandler>();

        services.AddOptions<HttpStandardResilienceOptions>(resilience.PipelineName)
            .Configure<IOptions<HuggingFaceOptions>>((pipeline, huggingFace) =>
            {
                var total = TimeSpan.FromSeconds(huggingFace.Value.TimeoutSeconds);
                var attempt = TimeSpan.FromSeconds(huggingFace.Value.AttemptTimeoutSeconds);
                pipeline.TotalRequestTimeout.Timeout = total;
                pipeline.AttemptTimeout.Timeout = attempt;
                // The library requires a sampling window of at least twice the attempt timeout.
                pipeline.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(Math.Max(30, 2 * attempt.TotalSeconds));
            });


        return services;
    }
}
