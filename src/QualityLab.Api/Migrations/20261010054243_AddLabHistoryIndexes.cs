using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QualityLab.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddLabHistoryIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "determination_reason_codes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    DeterminationId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Code = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_determination_reason_codes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_determination_reason_codes_determinations_DeterminationId",
                        column: x => x.DeterminationId,
                        principalTable: "determinations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "ix_determinations_batch_active_result",
                table: "determinations",
                columns: new[] { "BatchWorkItemId", "SupersededById", "Result" });

            migrationBuilder.CreateIndex(
                name: "ix_determinations_result",
                table: "determinations",
                column: "Result");

            migrationBuilder.CreateIndex(
                name: "ix_chemical_panels_product_line_tested_at",
                table: "chemical_panels",
                columns: new[] { "ProductLine", "TestedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "ix_chemical_panels_tested_at",
                table: "chemical_panels",
                column: "TestedAtUtc");

            migrationBuilder.CreateIndex(
                name: "ix_determination_reason_codes_code",
                table: "determination_reason_codes",
                column: "Code");

            migrationBuilder.CreateIndex(
                name: "ux_determination_reason_codes_det_code",
                table: "determination_reason_codes",
                columns: new[] { "DeterminationId", "Code" },
                unique: true);

            // Backfill: one row per code already stored in determinations.ReasonCodesJson, so
            // failures recorded before SCRUM-25 show up in the reason filter and counts.
            // INSERT IGNORE + the unique (DeterminationId, Code) index drops duplicate codes.
            migrationBuilder.Sql("""
                INSERT IGNORE INTO determination_reason_codes (Id, DeterminationId, Code)
                SELECT UUID(), d.Id, UPPER(TRIM(j.code))
                FROM determinations d
                JOIN JSON_TABLE(d.ReasonCodesJson, '$[*]' COLUMNS (code VARCHAR(50) PATH '$')) AS j
                WHERE d.ReasonCodesJson IS NOT NULL
                  AND JSON_VALID(d.ReasonCodesJson)
                  AND j.code IS NOT NULL
                  AND TRIM(j.code) <> '';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "determination_reason_codes");

            migrationBuilder.DropIndex(
                name: "ix_determinations_batch_active_result",
                table: "determinations");

            migrationBuilder.DropIndex(
                name: "ix_determinations_result",
                table: "determinations");

            migrationBuilder.DropIndex(
                name: "ix_chemical_panels_product_line_tested_at",
                table: "chemical_panels");

            migrationBuilder.DropIndex(
                name: "ix_chemical_panels_tested_at",
                table: "chemical_panels");
        }
    }
}
