using ComplianceMonitor.Application.Compliance;
using ComplianceMonitor.Application.Compliance.Models;
using ComplianceMonitor.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ComplianceMonitor.Infrastructure.Persistence.Configurations;

public sealed class AnalysisEntityConfiguration : IEntityTypeConfiguration<AnalysisEntity>
{
    public void Configure(EntityTypeBuilder<AnalysisEntity> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("Analyses");
        builder.Property(a => a.Action).HasMaxLength(ComplianceSettings.MaxTextLength);
        builder.Property(a => a.Guideline).HasMaxLength(ComplianceSettings.MaxTextLength);
        builder.Property(a => a.Result).HasConversion(v => DbNames.Of(v), v => DbNames.ToResult(v)).HasMaxLength(16);
        builder.Property(a => a.ModelTopResult).HasConversion(v => DbNames.Of(v!.Value), v => DbNames.ToResult(v)).HasMaxLength(16);
        builder.Property(a => a.DecisionSource).HasConversion(v => DbNames.Of(v), v => DbNames.ToSource(v)).HasMaxLength(16);
        builder.Property(a => a.DecisionReason).HasConversion(v => DbNames.Of(v), v => DbNames.ToReason(v)).HasMaxLength(32);
        builder.Property(a => a.ModelProvider).HasMaxLength(64);
        builder.Property(a => a.ModelId).HasMaxLength(128);
        builder.Property(a => a.Strategy).HasMaxLength(64);

        // SQLite stores DateTime as text without a kind; restore UTC on read so JSON ends in "Z".
        builder.Property(a => a.CreatedAt).HasConversion(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

        // History: ORDER BY CreatedAt DESC, Id DESC, optionally WHERE Result = ?. Summary: GROUP BY Result.
        builder.HasIndex(a => a.CreatedAt);
        builder.HasIndex(a => new { a.Result, a.CreatedAt });
    }
}

/// <summary>Explicit database names for the enums, so the table reads like the API and renaming a C# member can't change stored data.</summary>
public static class DbNames
{
    public static string Of(ComplianceResult value) => value switch
    {
        ComplianceResult.Complies => "COMPLIES",
        ComplianceResult.Deviates => "DEVIATES",
        ComplianceResult.Unclear => "UNCLEAR",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static string Of(DecisionSource value) => value switch
    {
        DecisionSource.Model => "MODEL",
        DecisionSource.Rule => "RULE",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static string Of(DecisionReason value) => value switch
    {
        DecisionReason.ModelClassification => "MODEL_CLASSIFICATION",
        DecisionReason.NoApplicableGuideline => "NO_APPLICABLE_GUIDELINE",
        DecisionReason.MissingTemporalEvidence => "MISSING_TEMPORAL_EVIDENCE",
        DecisionReason.InsufficientModelConfidence => "INSUFFICIENT_MODEL_CONFIDENCE",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static ComplianceResult ToResult(string value) => value switch
    {
        "COMPLIES" => ComplianceResult.Complies,
        "DEVIATES" => ComplianceResult.Deviates,
        "UNCLEAR" => ComplianceResult.Unclear,
        _ => throw new InvalidOperationException($"Unknown stored result '{value}'."),
    };

    public static DecisionSource ToSource(string value) => value switch
    {
        "MODEL" => DecisionSource.Model,
        "RULE" => DecisionSource.Rule,
        _ => throw new InvalidOperationException($"Unknown stored decision source '{value}'."),
    };

    public static DecisionReason ToReason(string value) => value switch
    {
        "MODEL_CLASSIFICATION" => DecisionReason.ModelClassification,
        "NO_APPLICABLE_GUIDELINE" => DecisionReason.NoApplicableGuideline,
        "MISSING_TEMPORAL_EVIDENCE" => DecisionReason.MissingTemporalEvidence,
        "INSUFFICIENT_MODEL_CONFIDENCE" => DecisionReason.InsufficientModelConfidence,
        _ => throw new InvalidOperationException($"Unknown stored decision reason '{value}'."),
    };
}
