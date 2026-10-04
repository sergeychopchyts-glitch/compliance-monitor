using ComplianceMonitor.Api.Classification;
using ComplianceMonitor.Api.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace ComplianceMonitor.Tests.Infrastructure;

/// <summary>
/// Hosts the API in-process with its own in-memory SQLite database, a fake clock and, by default,
/// a fake classifier. Set <see cref="HuggingFaceHandler"/> to keep the real classifier and fake HF's HTTP instead.
/// The "Testing" environment keeps user-secrets out, and the in-memory config source is added last so it
/// also wins over a HuggingFace__ApiToken environment variable. No test touches the network.
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>
{
    public const string FakeToken = "test-token-not-real";

    // Kept open for the factory's lifetime: a shared in-memory SQLite database lives as long as one connection does.
    private readonly SqliteConnection _keepAlive;
    private readonly string _connectionString;

    public ApiFactory()
    {
        _connectionString = $"Data Source=file:cm-{Guid.NewGuid():N}?mode=memory&cache=shared";
        _keepAlive = new SqliteConnection(_connectionString);
        _keepAlive.Open();
    }

    public string ConnectionString => _connectionString;

    public CapturingLoggerProvider Logs { get; } = new();

    public FakeComplianceClassifier Classifier { get; } = new();

    /// <summary>2026-10-03T10:15:00.789Z: the fraction checks that stored timestamps are truncated to seconds.</summary>
    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 3, 10, 15, 0, 789, TimeSpan.Zero));

    public FakeHttpMessageHandler? HuggingFaceHandler { get; init; }

    /// <summary>Null leaves the token to the app's own configuration sources (e.g. the environment).</summary>
    protected virtual string? ApiToken => FakeToken;

    public async Task<List<AnalysisRecord>> GetAnalysesAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ComplianceDbContext>();
        return await db.Analyses.AsNoTracking().OrderBy(a => a.Id).ToListAsync();
    }

    public async Task SeedAsync(params AnalysisRecord[] records)
    {
        foreach (var record in records)
        {
            using var scope = Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<AnalysisStore>().AddAsync(record, CancellationToken.None);
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        var settings = new Dictionary<string, string?>
        {
            [$"ConnectionStrings:{ComplianceDbContext.ConnectionStringName}"] = _connectionString,
        };
        if (ApiToken is not null)
        {
            settings["HuggingFace:ApiToken"] = ApiToken;
        }

        builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(settings));
        builder.ConfigureLogging(logging => logging.AddProvider(Logs));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Time);

            // Never the real network: either the test's fake, or a guard that fails loudly.
            var primary = HuggingFaceHandler ?? (HttpMessageHandler)new NoNetworkHandler();
            services.AddHttpClient<HuggingFaceZeroShotClient>().ConfigurePrimaryHttpMessageHandler(() => primary);

            if (HuggingFaceHandler is null)
            {
                services.RemoveAll<IComplianceClassifier>();
                services.AddSingleton<IComplianceClassifier>(Classifier);
            }
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _keepAlive.Dispose();
        }
    }
}

/// <summary>Stands in for the real network in non-Live tests; any request is a bug in the test.</summary>
public sealed class NoNetworkHandler : HttpMessageHandler
{
    public const string Message = "A non-Live test tried to reach the network. Use a fake handler or mark the test Live.";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(Message);
}

/// <summary>Returns <see cref="Outcome"/>, or throws <see cref="Throws"/> when set; records every call.</summary>
public sealed class FakeComplianceClassifier : IComplianceClassifier
{
    public ClassificationOutcome Outcome { get; set; } = new(
        ComplianceResult.Complies, 0.94, DecisionSource.Model, "fake",
        [new LabelScore("complies", 0.94), new LabelScore("violates", 0.06)]);

    public Exception? Throws { get; set; }

    public List<(string Action, string Guideline)> Calls { get; } = [];

    public Task<ClassificationOutcome> ClassifyAsync(string action, string guideline, CancellationToken cancellationToken)
    {
        Calls.Add((action, guideline));
        return Throws is null ? Task.FromResult(Outcome) : Task.FromException<ClassificationOutcome>(Throws);
    }
}
