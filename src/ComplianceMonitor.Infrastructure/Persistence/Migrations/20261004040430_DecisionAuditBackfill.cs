using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComplianceMonitor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DecisionAuditBackfill : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Convert existing rows. The old DecidedBy (now DecisionSource) mixed who decided with why.
            // Rows where the model was asked keep its score as ModelTopScore. The old threshold and the old
            // low-confidence top label weren't stored, so they stay NULL rather than being guessed.
            migrationBuilder.Sql("""
                UPDATE "Analyses" SET
                    "ModelProvider" = 'HuggingFace',
                    "ModelId" = 'facebook/bart-large-mnli',
                    "ModelTopScore" = "Confidence",
                    "ModelTopResult" = CASE "DecisionSource" WHEN 'MODEL' THEN "Result" ELSE NULL END
                WHERE "DecisionSource" IN ('MODEL', 'LOW_CONFIDENCE');
                """);
            migrationBuilder.Sql("""
                UPDATE "Analyses" SET "DecisionReason" = 'MODEL_CLASSIFICATION' WHERE "DecisionSource" = 'MODEL';
                """);
            migrationBuilder.Sql("""
                UPDATE "Analyses" SET "DecisionSource" = 'RULE', "DecisionReason" = 'INSUFFICIENT_MODEL_CONFIDENCE', "Confidence" = NULL
                WHERE "DecisionSource" = 'LOW_CONFIDENCE';
                """);
            // The old rule rows carried a made-up confidence of 1.0 and an empty score list.
            migrationBuilder.Sql("""
                UPDATE "Analyses" SET "DecisionReason" = 'NO_APPLICABLE_GUIDELINE', "Confidence" = NULL, "RawScoresJson" = NULL, "Strategy" = NULL
                WHERE "DecisionSource" = 'RULE' AND "DecisionReason" = '';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Data-only and one-way: the old DecidedBy values can't be fully reconstructed. Nothing to undo in the schema.
        }
    }
}
