using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QualityLab.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddDetermination : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CorrectionNote",
                table: "determinations");

            migrationBuilder.DropColumn(
                name: "IsSuperseded",
                table: "determinations");

            migrationBuilder.AddColumn<string>(
                name: "BatchCode",
                table: "determinations",
                type: "varchar(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "Notes",
                table: "determinations",
                type: "varchar(2000)",
                maxLength: 2000,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<Guid>(
                name: "SupersededById",
                table: "determinations",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.CreateIndex(
                name: "ix_determinations_batchcode",
                table: "determinations",
                column: "BatchCode");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_determinations_batchcode",
                table: "determinations");

            migrationBuilder.DropColumn(
                name: "BatchCode",
                table: "determinations");

            migrationBuilder.DropColumn(
                name: "Notes",
                table: "determinations");

            migrationBuilder.DropColumn(
                name: "SupersededById",
                table: "determinations");

            migrationBuilder.AddColumn<string>(
                name: "CorrectionNote",
                table: "determinations",
                type: "varchar(1000)",
                maxLength: 1000,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<bool>(
                name: "IsSuperseded",
                table: "determinations",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);
        }
    }
}
