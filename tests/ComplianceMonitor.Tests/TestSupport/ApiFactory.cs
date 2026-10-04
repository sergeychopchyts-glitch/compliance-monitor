using ComplianceMonitor.Application.Abstractions;
using ComplianceMonitor.Application.Compliance.Models;
using ComplianceMonitor.Infrastructure.Integrations.HuggingFace;
using ComplianceMonitor.Infrastructure.Persistence;
using ComplianceMonitor.Infrastructure.Persistence.Entities;
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

namespace ComplianceMonitor.Tests.TestSupport;

/// <summary>
/// Hosts the API in-process with its own in-memory SQLite database, a fake clock and, by default, a fake model
/// gateway: the real service, policies and repository run. Set <see cref="HuggingFaceHandler"/> to keep the real
/// Hugging Face gateway and fake only its HTTP.
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

    public FakeModelGateway Model { get; } = new();

    /// <summary>2026-10-03T10:15:00.789Z: the fraction checks that stored timestamps are truncated to seconds.</summary>
    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 3, 10, 15, 0, 789, TimeSpan.Zero));

    public FakeHttpMessageHandler? HuggingFaceHandler { get; init; }

    /// <summary>Null leaves the token to the app's own configuration sources (e.g. the environment).</summary>
    protected virtual string? ApiToken => FakeToken;

    public async Task<List<AnalysisEntity>> GetAnalysesAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ComplianceDbContext>();
        return await db.Analyses.AsNoTracking().OrderBy(a => a.Id).ToListAsync();
    }

    /// <summary>Stores analyses through the real repository, bypassing the model.</summary>
    public async Task SeedAsync(params NewAnalysis[] analyses)
    {
        foreach (var analysis in analyses)
        {
            using var scope = Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IAnalysisRepository>().AddAsync(analysis, CancellationToken.None);
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
                services.RemoveAll<IComplianceModelGateway>();
                services.AddSingleton<IComplianceModelGateway>(Model);
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

/// <summary>Stands in for the model: returns <see cref="Evaluation"/>, or throws <see cref="Throws"/>; records every call.</summary>
public sealed class FakeModelGateway : IComplianceModelGateway
{
    public static ModelEvaluation Scores(ComplianceResult top, double score) => new(
        "fake-provider", "fake-model", "fake-strategy",
        [
            new ModelScore("complies", ComplianceResult.Complies, top == ComplianceResult.Complies ? score : (1 - score) / 2),
            new ModelScore("violates", ComplianceResult.Deviates, top == ComplianceResult.Deviates ? score : (1 - score) / 2),
            new ModelScore("unrelated", ComplianceResult.Unclear, top == ComplianceResult.Unclear ? score : (1 - score) / 2),
        ]);

    public ModelEvaluation Evaluation { get; set; } = Scores(ComplianceResult.Complies, 0.94);

    public Exception? Throws { get; set; }

    /// <summary>When set, every call waits for it: lets a test hold requests "in flight".</summary>
    public TaskCompletionSource? Hold { get; set; }

    public List<(string Action, string Guideline)> Calls { get; } = [];

    public async Task<ModelEvaluation> EvaluateAsync(string action, string guideline, CancellationToken cancellationToken)
    {
        lock (Calls)
        {
            Calls.Add((action, guideline));
        }

        if (Hold is { } hold)
        {
            await hold.Task.WaitAsync(cancellationToken);
        }

        return Throws is null ? Evaluation : throw Throws;
    }
}
