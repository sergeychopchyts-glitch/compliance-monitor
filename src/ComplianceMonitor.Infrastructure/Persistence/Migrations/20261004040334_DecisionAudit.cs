using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComplianceMonitor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DecisionAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Hand-edited: the scaffold dropped ScoresJson and renamed DecidedBy without converting its values.
            migrationBuilder.DropIndex(
                name: "IX_Analyses_Result",
                table: "Analyses");

            migrationBuilder.RenameColumn(
                name: "ScoresJson",
                table: "Analyses",
                newName: "RawScoresJson");

            migrationBuilder.RenameColumn(
                name: "DecidedBy",
                table: "Analyses",
                newName: "DecisionSource");

            migrationBuilder.AlterColumn<string>(
                name: "Strategy",
                table: "Analyses",
                type: "TEXT",
                maxLength: 64,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 64);

            migrationBuilder.AlterColumn<double>(
                name: "Confidence",
                table: "Analyses",
                type: "REAL",
                nullable: true,
                oldClrType: typeof(double),
                oldType: "REAL");

            migrationBuilder.AlterColumn<string>(
                name: "RawScoresJson",
                table: "Analyses",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.AddColumn<double>(
                name: "ConfidenceThreshold",
                table: "Analyses",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DecisionReason",
                table: "Analyses",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ModelId",
                table: "Analyses",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ModelProvider",
                table: "Analyses",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ModelTopResult",
                table: "Analyses",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "ModelTopScore",
                table: "Analyses",
                type: "REAL",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Analyses_Result_CreatedAt",
                table: "Analyses",
                columns: new[] { "Result", "CreatedAt" });
            // Existing rows are converted in the next migration (DecisionAuditBackfill), after SQLite has rebuilt
            // the table with the new nullable columns; a rebuild runs at the end of a migration.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Analyses_Result_CreatedAt",
                table: "Analyses");

            migrationBuilder.DropColumn(
                name: "ConfidenceThreshold",
                table: "Analyses");

            migrationBuilder.DropColumn(
                name: "DecisionReason",
                table: "Analyses");

            migrationBuilder.DropColumn(
                name: "ModelId",
                table: "Analyses");

            migrationBuilder.DropColumn(
                name: "ModelProvider",
                table: "Analyses");

            migrationBuilder.DropColumn(
                name: "ModelTopResult",
                table: "Analyses");

            migrationBuilder.DropColumn(
                name: "ModelTopScore",
                table: "Analyses");

            migrationBuilder.DropColumn(
                name: "RawScoresJson",
                table: "Analyses");

            migrationBuilder.RenameColumn(
                name: "DecisionSource",
                table: "Analyses",
                newName: "DecidedBy");

            migrationBuilder.AlterColumn<string>(
                name: "Strategy",
                table: "Analyses",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 64,
                oldNullable: true);

            migrationBuilder.AlterColumn<double>(
                name: "Confidence",
                table: "Analyses",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0,
                oldClrType: typeof(double),
                oldType: "REAL",
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ScoresJson",
                table: "Analyses",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_Analyses_Result",
                table: "Analyses",
                column: "Result");
        }
    }
}
