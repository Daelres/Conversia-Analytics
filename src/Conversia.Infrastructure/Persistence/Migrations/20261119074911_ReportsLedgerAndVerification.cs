using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Conversia.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReportsLedgerAndVerification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ManagerReports_DefinitionId_PeriodFrom",
                table: "ManagerReports");

            migrationBuilder.AddColumn<int>(
                name: "ReportsWhatsAppCodeAttempts",
                table: "Users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReportsWhatsAppCodeExpiresAt",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReportsWhatsAppCodeHash",
                table: "Users",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReportsWhatsAppVerifiedAt",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ReportLedgerEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    DefinitionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Frequency = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    PeriodFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    Generator = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    FallbackReason = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    LlmAttempts = table.Column<int>(type: "integer", nullable: false),
                    InputTokens = table.Column<long>(type: "bigint", nullable: false),
                    OutputTokens = table.Column<long>(type: "bigint", nullable: false),
                    EstimatedUsd = table.Column<decimal>(type: "numeric(12,6)", precision: 12, scale: 6, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportLedgerEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReportLedgerEntries_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Users_ReportsWhatsAppVerified",
                table: "Users",
                sql: "\"ReportsWhatsAppVerifiedAt\" IS NULL OR \"ReportsWhatsAppNumber\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ManagerReports_DefinitionId_Frequency_PeriodFrom",
                table: "ManagerReports",
                columns: new[] { "DefinitionId", "Frequency", "PeriodFrom" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReportLedgerEntries_CreatedAt",
                table: "ReportLedgerEntries",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ReportLedgerEntries_DefinitionId_Frequency_PeriodFrom",
                table: "ReportLedgerEntries",
                columns: new[] { "DefinitionId", "Frequency", "PeriodFrom" });

            migrationBuilder.CreateIndex(
                name: "IX_ReportLedgerEntries_TenantId_CreatedAt",
                table: "ReportLedgerEntries",
                columns: new[] { "TenantId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReportLedgerEntries");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Users_ReportsWhatsAppVerified",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_ManagerReports_DefinitionId_Frequency_PeriodFrom",
                table: "ManagerReports");

            migrationBuilder.DropColumn(
                name: "ReportsWhatsAppCodeAttempts",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ReportsWhatsAppCodeExpiresAt",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ReportsWhatsAppCodeHash",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ReportsWhatsAppVerifiedAt",
                table: "Users");

            migrationBuilder.CreateIndex(
                name: "IX_ManagerReports_DefinitionId_PeriodFrom",
                table: "ManagerReports",
                columns: new[] { "DefinitionId", "PeriodFrom" },
                unique: true);
        }
    }
}
