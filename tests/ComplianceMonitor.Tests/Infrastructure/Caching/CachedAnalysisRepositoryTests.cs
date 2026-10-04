using ComplianceMonitor.Application.Compliance.Models;
using ComplianceMonitor.Infrastructure.Caching;
using ComplianceMonitor.Infrastructure.Persistence;
using ComplianceMonitor.Infrastructure.Persistence.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;

namespace ComplianceMonitor.Tests.Infrastructure.Caching;

public sealed class CachedAnalysisRepositoryTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly List<string> _sql = [];
    private readonly ComplianceDbContext _db;
    private readonly ServiceProvider _provider;

    public CachedAnalysisRepositoryTests()
    {
        _connection.Open();
        _db = new ComplianceDbContext(new DbContextOptionsBuilder<ComplianceDbContext>()
            .UseSqlite(_connection)
            .LogTo(_sql.Add, [DbLoggerCategory.Database.Command.Name], Microsoft.Extensions.Logging.LogLevel.Information)
            .Options);
        _db.Database.Migrate();
        _provider = new ServiceCollection().AddHybridCache().Services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _db.Dispose();
        _provider.Dispose();
        _connection.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private CachedAnalysisRepository CreateRepository() => new(new AnalysisRepository(_db), _provider.GetRequiredService<HybridCache>());

    private int SummaryQueries => _sql.Count(s => s.Contains("GROUP BY", StringComparison.Ordinal));

    private static NewAnalysis Analysis(ComplianceResult result) => new(
        "a", "g", new ComplianceDecision(result, 0.9, DecisionSource.Model, DecisionReason.ModelClassification), null, null,
        new DateTime(2026, 10, 3, 10, 0, 0, DateTimeKind.Utc));

    [Fact]
    public async Task GetSummaryAsync_SecondReadIsServedFromCache()
    {
        var repository = CreateRepository();

        var first = await repository.GetSummaryAsync(Ct);
        var second = await repository.GetSummaryAsync(Ct);

        Assert.Equal(first, second);
        Assert.Equal(1, SummaryQueries);
    }

    [Fact]
    public async Task AddAsync_InvalidatesTheCachedSummary()
    {
        var repository = CreateRepository();
        Assert.Equal(new AnalysisSummary(0, 0, 0, 0), await repository.GetSummaryAsync(Ct));

        await repository.AddAsync(Analysis(ComplianceResult.Deviates), Ct);
        var after = await repository.GetSummaryAsync(Ct);

        Assert.Equal(new AnalysisSummary(1, 0, 1, 0), after);
        Assert.Equal(2, SummaryQueries);
    }

    [Fact]
    public void Summary_UsesAVersionedKeyAndAShortLifetime()
    {
        Assert.Equal("analysis:summary:v1", CacheKeys.AnalysisSummary);
        Assert.InRange(CachedAnalysisRepository.SummaryLifetime, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60));
    }
}
