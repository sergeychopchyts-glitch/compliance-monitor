using ComplianceMonitor.Api.Classification;
using ComplianceMonitor.Api.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ComplianceMonitor.Tests.Persistence;

/// <summary>
/// Real SQLite in memory (not the EF InMemory provider, which hides SQLite behaviour). The connection
/// is held open for the test so the database survives across contexts; the schema comes from the migration.
/// </summary>
public sealed class AnalysisStoreTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 3, 10, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly DbContextOptions<ComplianceDbContext> _options;

    public AnalysisStoreTests()
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<ComplianceDbContext>().UseSqlite(_connection).Options;
        using var db = NewContext();
        db.Database.Migrate();
    }

    public void Dispose() => _connection.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // A fresh context per call, so reads really come from SQLite and not from the change tracker.
    private ComplianceDbContext NewContext() => new(_options);

    private async Task<T> WithStore<T>(Func<AnalysisStore, Task<T>> act)
    {
        await using var db = NewContext();
        return await act(new AnalysisStore(db));
    }

    private static AnalysisRecord Record(
        DateTime createdAt, ComplianceResult result = ComplianceResult.Complies, string action = "a") => new()
        {
            Action = action,
            Guideline = "g",
            Result = result,
            Confidence = 0.9238446950912476,
            CreatedAt = createdAt,
            Strategy = "placeholder",
            DecidedBy = DecisionSource.Model,
            ScoresJson = "[]",
        };

    private async Task Seed(params AnalysisRecord[] records)
    {
        foreach (var record in records)
        {
            await WithStore(store => store.AddAsync(record, Ct));
        }
    }

    [Fact]
    public async Task AddAsync_AssignsIdAndRoundTripsEveryField()
    {
        var added = await WithStore(store => store.AddAsync(Record(T0, ComplianceResult.Deviates), Ct));

        var stored = Assert.Single(await WithStore(store => store.GetHistoryAsync(10, 0, null, Ct)));
        Assert.True(added.Id > 0);
        Assert.Equal(added.Id, stored.Id);
        Assert.Equal(ComplianceResult.Deviates, stored.Result);
        Assert.Equal(0.9238446950912476, stored.Confidence);
        Assert.Equal(DecisionSource.Model, stored.DecidedBy);
    }

    [Fact]
    public async Task CreatedAt_KeepsUtcKindAfterRoundTrip()
    {
        await Seed(Record(T0));

        var stored = Assert.Single(await WithStore(store => store.GetHistoryAsync(10, 0, null, Ct)));

        Assert.Equal(DateTimeKind.Utc, stored.CreatedAt.Kind);
        Assert.Equal(T0, stored.CreatedAt);
        Assert.Equal("\"2026-10-03T10:00:00Z\"", System.Text.Json.JsonSerializer.Serialize(stored.CreatedAt));
    }

    [Fact]
    public async Task Enums_AreStoredAsApiNames()
    {
        var record = Record(T0, ComplianceResult.Unclear);
        record.DecidedBy = DecisionSource.LowConfidence;
        await Seed(record);

        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT Result || '/' || DecidedBy FROM Analyses";
        Assert.Equal("UNCLEAR/LOW_CONFIDENCE", (string?)await command.ExecuteScalarAsync(Ct));
    }

    [Fact]
    public async Task Migration_CreatesIndexesOnCreatedAtAndResult()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = 'Analyses' ORDER BY name";
        var names = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct))
            {
                names.Add(reader.GetString(0));
            }
        }

        Assert.Equal(["IX_Analyses_CreatedAt", "IX_Analyses_Result"], names);
    }

    [Fact]
    public async Task GetHistoryAsync_IsNewestFirstRegardlessOfInsertOrder()
    {
        await Seed(Record(T0.AddMinutes(1), action: "middle"), Record(T0.AddMinutes(2), action: "newest"), Record(T0, action: "oldest"));

        var history = await WithStore(store => store.GetHistoryAsync(10, 0, null, Ct));

        Assert.Equal(["newest", "middle", "oldest"], history.Select(h => h.Action));
    }

    [Fact]
    public async Task GetHistoryAsync_EqualTimestamps_HigherIdFirst()
    {
        await Seed(Record(T0, action: "first"), Record(T0, action: "second"), Record(T0, action: "third"));

        var history = await WithStore(store => store.GetHistoryAsync(10, 0, null, Ct));

        Assert.Equal(["third", "second", "first"], history.Select(h => h.Action));
    }

    [Theory]
    [InlineData(2, 0, new[] { "4", "3" })]
    [InlineData(2, 1, new[] { "3", "2" })]
    [InlineData(10, 3, new[] { "1", "0" })]
    [InlineData(1, 5, new string[0])]
    public async Task GetHistoryAsync_AppliesLimitAndOffset(int limit, int offset, string[] expected)
    {
        await Seed([.. Enumerable.Range(0, 5).Select(i => Record(T0.AddMinutes(i), action: i.ToString(System.Globalization.CultureInfo.InvariantCulture)))]);

        var history = await WithStore(store => store.GetHistoryAsync(limit, offset, null, Ct));

        Assert.Equal(expected, history.Select(h => h.Action));
    }

    [Fact]
    public async Task GetHistoryAsync_FiltersByResult()
    {
        await Seed(
            Record(T0, ComplianceResult.Deviates, "d1"),
            Record(T0.AddMinutes(1), ComplianceResult.Complies, "c1"),
            Record(T0.AddMinutes(2), ComplianceResult.Deviates, "d2"),
            Record(T0.AddMinutes(3), ComplianceResult.Unclear, "u1"));

        var deviates = await WithStore(store => store.GetHistoryAsync(10, 0, ComplianceResult.Deviates, Ct));
        var firstDeviate = await WithStore(store => store.GetHistoryAsync(1, 1, ComplianceResult.Deviates, Ct));

        Assert.Equal(["d2", "d1"], deviates.Select(h => h.Action));
        Assert.Equal(["d1"], firstDeviate.Select(h => h.Action));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, -1)]
    public async Task GetHistoryAsync_InvalidPaging_Throws(int limit, int offset) =>
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => WithStore(store => store.GetHistoryAsync(limit, offset, null, Ct)));

    [Fact]
    public async Task GetSummaryAsync_EmptyDatabase_ReturnsThreeZeros()
    {
        var summary = await WithStore(store => store.GetSummaryAsync(Ct));

        Assert.Equal(0, summary.Total);
        Assert.Equal(
            new Dictionary<ComplianceResult, int> { [ComplianceResult.Complies] = 0, [ComplianceResult.Deviates] = 0, [ComplianceResult.Unclear] = 0 },
            summary.ByResult);
    }

    [Fact]
    public async Task GetSummaryAsync_CountsEachResultAndFillsMissingWithZero()
    {
        await Seed(
            Record(T0, ComplianceResult.Deviates),
            Record(T0, ComplianceResult.Complies),
            Record(T0, ComplianceResult.Deviates));

        var summary = await WithStore(store => store.GetSummaryAsync(Ct));

        Assert.Equal(3, summary.Total);
        Assert.Equal(1, summary.ByResult[ComplianceResult.Complies]);
        Assert.Equal(2, summary.ByResult[ComplianceResult.Deviates]);
        Assert.Equal(0, summary.ByResult[ComplianceResult.Unclear]);
    }
}
