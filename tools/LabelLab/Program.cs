// Measures label strategies against the live Hugging Face API.
// Usage: dotnet run --project tools/LabelLab [-- --strategy <name>]
using ComplianceMonitor.Api.Classification;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ComplianceMonitor.LabelLab;

// An explicit Main in a namespace: a top-level Program would clash with the API's Program in the test project.
internal static class Program
{
    private const string ApiUserSecretsId = "compliance-monitor-api";

    public static async Task<int> Main(string[] args)
    {
        var root = FindRepoRoot();
        if (root is null)
        {
            Console.Error.WriteLine("Run LabelLab from inside the repository (ComplianceMonitor.slnx not found).");
            return 2;
        }

        // Same keys and sources as the API: appsettings.json, user-secrets, HuggingFace__* environment variables.
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(root, "src", "ComplianceMonitor.Api", "appsettings.json"), optional: true)
            .AddUserSecrets(ApiUserSecretsId)
            .AddEnvironmentVariables()
            .AddCommandLine(args)
            .Build();

        var options = new HuggingFaceOptions();
        configuration.GetSection(HuggingFaceOptions.SectionName).Bind(options);
        var validation = new HuggingFaceOptionsValidator().Validate(null, options);
        if (validation.Failed)
        {
            Console.Error.WriteLine(validation.FailureMessage);
            return 2;
        }

        var strategyFilter = configuration["strategy"];
        var strategies = typeof(ILabelStrategy).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ILabelStrategy).IsAssignableFrom(t)
                && t.GetConstructor(Type.EmptyTypes) is not null)
            .Select(t => (ILabelStrategy)Activator.CreateInstance(t)!)
            .Where(s => strategyFilter is null || s.Name.Equals(strategyFilter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.Name, StringComparer.Ordinal)
            .ToList();
        if (strategies.Count == 0)
        {
            Console.Error.WriteLine($"No label strategy found{(strategyFilter is null ? "" : $" named '{strategyFilter}'")}.");
            return 2;
        }

        IReadOnlyList<LabCase> cases;
        try
        {
            cases = LabCase.Parse(await File.ReadAllTextAsync(Path.Combine(root, "tools", "LabelLab", "cases.json")));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            Console.Error.WriteLine($"Could not load tools/LabelLab/cases.json: {ex.Message}");
            return 2;
        }

        using var cacheHandler = new CachingThrottlingHandler(
            Path.Combine(root, ".cache", "hf"), TimeSpan.FromMilliseconds(300), TimeProvider.System);

        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(options));
        services.AddHttpClient<HuggingFaceZeroShotClient>(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .AddHttpMessageHandler(() => cacheHandler) // outermost: cache hits skip retries and throttling
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan) // the single handler instance must never be rebuilt
            .AddStandardResilienceHandler(pipeline =>
            {
                // Generous: a cold bart-large-mnli can take a while to load.
                pipeline.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(120);
                pipeline.AttemptTimeout.Timeout = TimeSpan.FromSeconds(60);
                pipeline.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(120);
            });
        await using var provider = services.BuildServiceProvider();

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        IReadOnlyList<StrategyReport> reports;
        try
        {
            var runner = new LabRunner(provider.GetRequiredService<HuggingFaceZeroShotClient>(), options.ConfidenceFloor);
            reports = await runner.RunAsync(strategies, cases, cts.Token);
        }
        catch (CreditsExhaustedException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 3;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            Console.Error.WriteLine("Cancelled; cached results are kept.");
            return 130;
        }

        var markdown = MarkdownReport.Render(reports, options.Model, options.ConfidenceFloor, TimeProvider.System.GetUtcNow());
        Console.WriteLine(markdown);
        await File.WriteAllTextAsync(Path.Combine(root, "docs", "label-tuning.md"), markdown);
        Console.WriteLine($"Wrote docs/label-tuning.md. Cache hits: {cacheHandler.CacheHits}, network calls: {cacheHandler.NetworkCalls}.");
        return 0;
    }

    private static string? FindRepoRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "ComplianceMonitor.slnx")))
                {
                    return dir.FullName;
                }
            }
        }

        return null;
    }
}
