using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

namespace ComplianceMonitor.Api.Classification;

public static class ClassificationServiceCollectionExtensions
{
    public static IServiceCollection AddComplianceClassification(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<HuggingFaceOptions>()
            .Bind(configuration.GetSection(HuggingFaceOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<HuggingFaceOptions>, HuggingFaceOptionsValidator>();

        var resilience = services
            .AddHttpClient<HuggingFaceZeroShotClient>(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .AddStandardResilienceHandler();

        // Total budget from config; each attempt gets half so at least one retry fits.
        services.AddOptions<HttpStandardResilienceOptions>(resilience.PipelineName)
            .Configure<IOptions<HuggingFaceOptions>>((pipeline, huggingFace) =>
            {
                var total = TimeSpan.FromSeconds(huggingFace.Value.TimeoutSeconds);
                pipeline.TotalRequestTimeout.Timeout = total;
                pipeline.AttemptTimeout.Timeout = total / 2;
                pipeline.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(Math.Max(30, total.TotalSeconds));
            });

        services.AddSingleton<ILabelStrategy, PlaceholderLabelStrategy>();
        services.AddTransient<IComplianceClassifier, ComplianceClassifier>();

        return services;
    }
}
