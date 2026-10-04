using System.Globalization;
using ComplianceMonitor.Application.Compliance.Models;
using ComplianceMonitor.Infrastructure.Persistence;
using ComplianceMonitor.Infrastructure.Persistence.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ComplianceMonitor.Tests.Infrastructure.Persistence;

/// <summary>
/// Real SQLite in memory (not the EF InMemory provider, which hides SQLite behaviour). The connection
/// is held open for the test so the database survives across contexts; the schema comes from the migrations.
/// </summary>
public sealed class AnalysisRepositoryTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 3, 10, 0, 0, DateTimeKind.Utc);

    private static readonly ModelEvaluation Evaluation = new(
        "HuggingFace", "facebook/bart-large-mnli", "combined-three-label-v1",
        [
            new ModelScore("complies with the guideline", ComplianceResult.Complies, 0.9238446950912476),
            new ModelScore("violates the guideline", ComplianceResult.Deviates, 0.05),
            new ModelScore("is unrelated to the guideline", ComplianceResult.Unclear, 0.0261553049087524),
        ]);

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly DbContextOptions<ComplianceDbContext> _options;
    private readonly List<string> _sql = [];

    public AnalysisRepositoryTests()
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<ComplianceDbContext>()
            .UseSqlite(_connection)
            .LogTo(_sql.Add, [DbLoggerCategory.Database.Command.Name], Microsoft.Extensions.Logging.LogLevel.Information)
            .Options;
        using var db = NewContext();
        db.Database.Migrate();
    }

    public void Dispose() => _connection.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // A fresh context per call, so reads really come from SQLite and not from the change tracker.
    private ComplianceDbContext NewContext() => new(_options);

    private async Task<T> WithRepository<T>(Func<AnalysisRepository, Task<T>> act)
    {
        await using var db = NewContext();
        return await act(new AnalysisRepository(db));
    }

    private static NewAnalysis ByModel(DateTime createdAt, ComplianceResult result = ComplianceResult.Complies, string action = "a") =>
        new(action, "g", new ComplianceDecision(result, 0.9238446950912476, DecisionSource.Model, DecisionReason.ModelClassification),
            Evaluation, 0.5, createdAt);

    private static NewAnalysis ByRule(DateTime createdAt) =>
        new("a", "No guidelines exist for this case.",
            new ComplianceDecision(ComplianceResult.Unclear, null, DecisionSource.Rule, DecisionReason.NoApplicableGuideline),
            null, null, createdAt);

    private async Task Seed(params NewAnalysis[] analyses)
    {
        foreach (var analysis in analyses)
        {
            await WithRepository(repository => repository.AddAsync(analysis, Ct));
        }
    }

    private async Task<string?> Scalar(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(Ct), CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task AddAsync_RoundTripsTheResultFields()
    {
        var added = await WithRepository(r => r.AddAsync(ByModel(T0, ComplianceResult.Deviates), Ct));

        var stored = Assert.Single(await WithRepository(r => r.GetHistoryAsync(10, 0, null, Ct)));
        Assert.True(added.Id > 0);
        Assert.Equal(added, stored);
        Assert.Equal(0.9238446950912476, stored.Confidence);
    }

    [Fact]
    public async Task AddAsync_StoresTheAuditTrail()
    {
        await Seed(ByModel(T0));

        Assert.Equal(
            "HuggingFace|facebook/bart-large-mnli|combined-three-label-v1|COMPLIES",
            await Scalar("""SELECT ModelProvider || '|' || ModelId || '|' || Strategy || '|' || ModelTopResult FROM Analyses"""));
        Assert.Equal(0.9238446950912476, double.Parse((await Scalar("SELECT ModelTopScore FROM Analyses"))!, CultureInfo.InvariantCulture));
        Assert.Equal(0.5, double.Parse((await Scalar("SELECT ConfidenceThreshold FROM Analyses"))!, CultureInfo.InvariantCulture));
        var scores = await Scalar("SELECT RawScoresJson FROM Analyses");
        Assert.Contains("""{"label":"is unrelated to the guideline","result":2,"score":0.0261553049087524}""", scores, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddAsync_RuleDecision_StoresNoModelFieldsAndNoConfidence()
    {
        await Seed(ByRule(T0));

        Assert.Equal("RULE|NO_APPLICABLE_GUIDELINE|null|null|null",
            await Scalar("""SELECT DecisionSource || '|' || DecisionReason || '|' || IFNULL(Confidence, 'null') || '|' || IFNULL(ModelId, 'null') || '|' || IFNULL(RawScoresJson, 'null') FROM Analyses"""));
        var stored = Assert.Single(await WithRepository(r => r.GetHistoryAsync(10, 0, null, Ct)));
        Assert.Null(stored.Confidence);
    }

    [Fact]
    public async Task Enums_AreStoredByExplicitApiNames()
    {
        await Seed(ByModel(T0, ComplianceResult.Unclear));

        Assert.Equal("UNCLEAR/MODEL/MODEL_CLASSIFICATION", await Scalar("SELECT Result || '/' || DecisionSource || '/' || DecisionReason FROM Analyses"));
    }

    [Fact]
    public async Task CreatedAt_KeepsUtcKindAfterRoundTrip()
    {
        await Seed(ByModel(T0));

        var stored = Assert.Single(await WithRepository(r => r.GetHistoryAsync(10, 0, null, Ct)));

        Assert.Equal(DateTimeKind.Utc, stored.CreatedAt.Kind);
        Assert.Equal(T0, stored.CreatedAt);
    }

    [Fact]
    public async Task Migrations_CreateTheHistoryIndexes()
    {
        Assert.Equal(
            "IX_Analyses_CreatedAt,IX_Analyses_Result_CreatedAt",
            await Scalar("SELECT group_concat(name) FROM (SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = 'Analyses' ORDER BY name)"));
    }

    [Fact]
    public void Migrations_MatchTheModel()
    {
        using var db = NewContext();

        Assert.False(db.Database.HasPendingModelChanges(), "The model changed without a migration: run dotnet ef migrations add.");
        Assert.Empty(db.Database.GetPendingMigrations());
    }

    [Fact]
    public async Task Migrations_UpgradeRowsStoredBeforeTheAuditColumns()
    {
        // A database still at InitialCreate, with the old DecidedBy values mixing who decided with why.
        await using var old = new SqliteConnection("Data Source=:memory:");
        await old.OpenAsync(Ct);
        var options = new DbContextOptionsBuilder<ComplianceDbContext>().UseSqlite(old).Options;
        await using (var db = new ComplianceDbContext(options))
        {
            await db.GetService<IMigrator>().MigrateAsync("20261004003218_InitialCreate", Ct);
        }

        using (var insert = old.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO Analyses (Action, Guideline, Result, Confidence, CreatedAt, Strategy, DecidedBy, ScoresJson) VALUES
                ('a1', 'g', 'COMPLIES', 0.9, '2026-10-03 10:00:00', 'placeholder', 'MODEL', '[{"label":"x","score":0.9}]'),
                ('a2', 'No guidelines exist for this case.', 'UNCLEAR', 1.0, '2026-10-03 10:01:00', 'placeholder', 'RULE', '[]'),
                ('a3', 'g', 'UNCLEAR', 0.45, '2026-10-03 10:02:00', 'placeholder', 'LOW_CONFIDENCE', '[{"label":"x","score":0.45}]');
                """;
            await insert.ExecuteNonQueryAsync(Ct);
        }

        await using (var db = new ComplianceDbContext(options))
        {
            await db.Database.MigrateAsync(Ct);
        }

        using var read = old.CreateCommand();
        read.CommandText = """
            SELECT Action || '|' || DecisionSource || '|' || DecisionReason || '|' || IFNULL(Confidence, 'null') || '|' ||
                   IFNULL(ModelTopResult, 'null') || '|' || IFNULL(ModelTopScore, 'null') || '|' || IFNULL(ModelId, 'null') || '|' ||
                   IFNULL(RawScoresJson, 'null') || '|' || IFNULL(Strategy, 'null')
            FROM Analyses ORDER BY Id
            """;
        var rows = new List<string>();
        await using (var reader = await read.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct))
            {
                rows.Add(reader.GetString(0));
            }
        }

        Assert.Equal(
            [
                """a1|MODEL|MODEL_CLASSIFICATION|0.9|COMPLIES|0.9|facebook/bart-large-mnli|[{"label":"x","score":0.9}]|placeholder""",
                "a2|RULE|NO_APPLICABLE_GUIDELINE|null|null|null|null|null|null",                         // the made-up 1.0 is gone
                """a3|RULE|INSUFFICIENT_MODEL_CONFIDENCE|null|null|0.45|facebook/bart-large-mnli|[{"label":"x","score":0.45}]|placeholder""",
            ],
            rows);
    }

    [Fact]
    public async Task GetHistoryAsync_IsNewestFirstRegardlessOfInsertOrder()
    {
        await Seed(ByModel(T0.AddMinutes(1), action: "middle"), ByModel(T0.AddMinutes(2), action: "newest"), ByModel(T0, action: "oldest"));

        var history = await WithRepository(r => r.GetHistoryAsync(10, 0, null, Ct));

        Assert.Equal(["newest", "middle", "oldest"], history.Select(h => h.Action));
    }

    [Fact]
    public async Task GetHistoryAsync_EqualTimestamps_HigherIdFirst()
    {
        await Seed(ByModel(T0, action: "first"), ByModel(T0, action: "second"), ByModel(T0, action: "third"));

        var history = await WithRepository(r => r.GetHistoryAsync(10, 0, null, Ct));

        Assert.Equal(["third", "second", "first"], history.Select(h => h.Action));
    }

    [Theory]
    [InlineData(2, 0, new[] { "4", "3" })]
    [InlineData(2, 1, new[] { "3", "2" })]
    [InlineData(10, 3, new[] { "1", "0" })]
    [InlineData(1, 5, new string[0])]
    public async Task GetHistoryAsync_AppliesLimitAndOffset(int limit, int offset, string[] expected)
    {
        await Seed([.. Enumerable.Range(0, 5).Select(i => ByModel(T0.AddMinutes(i), action: i.ToString(CultureInfo.InvariantCulture)))]);

        var history = await WithRepository(r => r.GetHistoryAsync(limit, offset, null, Ct));

        Assert.Equal(expected, history.Select(h => h.Action));
    }

    [Fact]
    public async Task GetHistoryAsync_FiltersByResult()
    {
        await Seed(
            ByModel(T0, ComplianceResult.Deviates, "d1"),
            ByModel(T0.AddMinutes(1), ComplianceResult.Complies, "c1"),
            ByModel(T0.AddMinutes(2), ComplianceResult.Deviates, "d2"),
            ByModel(T0.AddMinutes(3), ComplianceResult.Unclear, "u1"));

        var deviates = await WithRepository(r => r.GetHistoryAsync(10, 0, ComplianceResult.Deviates, Ct));
        var secondDeviate = await WithRepository(r => r.GetHistoryAsync(1, 1, ComplianceResult.Deviates, Ct));

        Assert.Equal(["d2", "d1"], deviates.Select(h => h.Action));
        Assert.Equal(["d1"], secondDeviate.Select(h => h.Action));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, -1)]
    public async Task GetHistoryAsync_InvalidPaging_Throws(int limit, int offset) =>
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => WithRepository(r => r.GetHistoryAsync(limit, offset, null, Ct)));

    [Fact]
    public async Task GetSummaryAsync_EmptyDatabase_ReturnsThreeZeros() =>
        Assert.Equal(new AnalysisSummary(0, 0, 0, 0), await WithRepository(r => r.GetSummaryAsync(Ct)));

    [Fact]
    public async Task GetSummaryAsync_CountsEachResultAndFillsMissingWithZero()
    {
        await Seed(ByModel(T0, ComplianceResult.Deviates), ByModel(T0, ComplianceResult.Complies), ByModel(T0, ComplianceResult.Deviates));

        Assert.Equal(new AnalysisSummary(3, 1, 2, 0), await WithRepository(r => r.GetSummaryAsync(Ct)));
    }

    [Fact]
    public async Task GetSummaryAsync_RunsOneGroupByQueryInSql()
    {
        await Seed(ByModel(T0, ComplianceResult.Deviates), ByModel(T0, ComplianceResult.Complies));
        _sql.Clear();

        await WithRepository(r => r.GetSummaryAsync(Ct));

        Assert.Contains("GROUP BY", Assert.Single(_sql), StringComparison.Ordinal);
    }
}
