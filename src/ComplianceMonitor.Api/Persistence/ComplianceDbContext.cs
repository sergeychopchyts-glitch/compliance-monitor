using System.Text.Json;
using ComplianceMonitor.Api.Classification;
using Microsoft.EntityFrameworkCore;

namespace ComplianceMonitor.Api.Persistence;

public sealed class AnalysisRecord
{
    public const int MaxTextLength = 2000;

    public long Id { get; set; }

    public required string Action { get; set; }

    public required string Guideline { get; set; }

    public ComplianceResult Result { get; set; }

    public double Confidence { get; set; }

    /// <summary>Always <see cref="DateTimeKind.Utc"/>, whole seconds.</summary>
    public DateTime TimestampUtc { get; set; }

    /// <summary>Audit: the label strategy in effect.</summary>
    public required string Strategy { get; set; }

    /// <summary>Audit: what decided the result.</summary>
    public DecisionSource DecidedBy { get; set; }

    /// <summary>Audit: the raw HF scores as JSON; "[]" when a rule decided.</summary>
    public required string ScoresJson { get; set; }
}

public sealed class ComplianceDbContext(DbContextOptions<ComplianceDbContext> options) : DbContext(options)
{
    public const string ConnectionStringName = "ComplianceMonitor";

    public DbSet<AnalysisRecord> Analyses => Set<AnalysisRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var analysis = modelBuilder.Entity<AnalysisRecord>();
        analysis.ToTable("Analyses");
        analysis.Property(a => a.Action).HasMaxLength(AnalysisRecord.MaxTextLength);
        analysis.Property(a => a.Guideline).HasMaxLength(AnalysisRecord.MaxTextLength);
        analysis.Property(a => a.Result)
            .HasConversion(v => EnumText.ToText(v), v => EnumText.Parse<ComplianceResult>(v))
            .HasMaxLength(16);
        analysis.Property(a => a.DecidedBy)
            .HasConversion(v => EnumText.ToText(v), v => EnumText.Parse<DecisionSource>(v))
            .HasMaxLength(16);
        analysis.Property(a => a.Strategy).HasMaxLength(64);

        // SQLite stores DateTime as text without a kind; restore UTC on read so JSON ends in "Z".
        analysis.Property(a => a.TimestampUtc).HasConversion(
            v => v,
            v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
    }
}

/// <summary>Stores enums by their JSON names (COMPLIES, LOW_CONFIDENCE, ...) so the table reads like the API.</summary>
public static class EnumText
{
    public static string ToText<TEnum>(TEnum value)
        where TEnum : struct, Enum =>
        JsonSerializer.Deserialize<string>(JsonSerializer.Serialize(value))!;

    public static TEnum Parse<TEnum>(string text)
        where TEnum : struct, Enum =>
        JsonSerializer.Deserialize<TEnum>(JsonSerializer.Serialize(text));
}
