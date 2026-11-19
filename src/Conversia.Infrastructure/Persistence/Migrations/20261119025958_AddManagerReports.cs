using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Conversia.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddManagerReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReportsWhatsAppNumber",
                table: "Users",
                type: "character varying(15)",
                maxLength: 15,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReportsWhatsAppOptInAt",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReportsWhatsAppOptOutAt",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ReportDefinitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Frequency = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SendDayOfWeek = table.Column<int>(type: "integer", nullable: false),
                    SendHour = table.Column<int>(type: "integer", nullable: false),
                    BranchIds = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    Sections = table.Column<int>(type: "integer", nullable: false),
                    WhatsAppChannelId = table.Column<Guid>(type: "uuid", nullable: true),
                    WhatsAppTemplateName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    WhatsAppTemplateLanguage = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportDefinitions", x => x.Id);
                    table.CheckConstraint("CK_ReportDefinitions_SendDayOfWeek", "\"SendDayOfWeek\" BETWEEN 1 AND 7");
                    table.CheckConstraint("CK_ReportDefinitions_SendHour", "\"SendHour\" BETWEEN 0 AND 23");
                    table.ForeignKey(
                        name: "FK_ReportDefinitions_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ManagerReports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    DefinitionId = table.Column<Guid>(type: "uuid", nullable: false),
                    DefinitionName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Frequency = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    PeriodFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    PeriodTo = table.Column<DateOnly>(type: "date", nullable: false),
                    IsSandbox = table.Column<bool>(type: "boolean", nullable: false),
                    Generator = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    FallbackReason = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    LlmAttempts = table.Column<int>(type: "integer", nullable: false),
                    Model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    InputTokens = table.Column<long>(type: "bigint", nullable: false),
                    OutputTokens = table.Column<long>(type: "bigint", nullable: false),
                    EstimatedUsd = table.Column<decimal>(type: "numeric(12,6)", precision: 12, scale: 6, nullable: false),
                    DataPackVersion = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Content = table.Column<string>(type: "jsonb", nullable: false),
                    DataPack = table.Column<string>(type: "jsonb", nullable: false),
                    Html = table.Column<string>(type: "text", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    Headline = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    GeneratedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManagerReports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ManagerReports_ReportDefinitions_DefinitionId",
                        column: x => x.DefinitionId,
                        principalTable: "ReportDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ManagerReports_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ReportRecipients",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    DefinitionId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Channels = table.Column<int>(type: "integer", nullable: false),
                    EmailUnsubscribedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportRecipients", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReportRecipients_ReportDefinitions_DefinitionId",
                        column: x => x.DefinitionId,
                        principalTable: "ReportDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ReportRecipients_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ReportDeliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReportId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Channel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ProviderMessageId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportDeliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReportDeliveries_ManagerReports_ReportId",
                        column: x => x.ReportId,
                        principalTable: "ManagerReports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ReportDeliveries_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Users_ReportsWhatsAppOptIn",
                table: "Users",
                sql: "(\"ReportsWhatsAppNumber\" IS NULL) = (\"ReportsWhatsAppOptInAt\" IS NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_ManagerReports_DefinitionId_PeriodFrom",
                table: "ManagerReports",
                columns: new[] { "DefinitionId", "PeriodFrom" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ManagerReports_GeneratedAt",
                table: "ManagerReports",
                column: "GeneratedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ManagerReports_TenantId_GeneratedAt",
                table: "ManagerReports",
                columns: new[] { "TenantId", "GeneratedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ReportDefinitions_IsActive_TenantId",
                table: "ReportDefinitions",
                columns: new[] { "IsActive", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_ReportDefinitions_TenantId",
                table: "ReportDefinitions",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ReportDeliveries_ReportId_UserId_Channel",
                table: "ReportDeliveries",
                columns: new[] { "ReportId", "UserId", "Channel" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReportDeliveries_Status_NextAttemptAt",
                table: "ReportDeliveries",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ReportDeliveries_TenantId",
                table: "ReportDeliveries",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ReportDeliveries_UserId",
                table: "ReportDeliveries",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ReportRecipients_DefinitionId_UserId",
                table: "ReportRecipients",
                columns: new[] { "DefinitionId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReportRecipients_TenantId",
                table: "ReportRecipients",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ReportRecipients_UserId",
                table: "ReportRecipients",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReportDeliveries");

            migrationBuilder.DropTable(
                name: "ReportRecipients");

            migrationBuilder.DropTable(
                name: "ManagerReports");

            migrationBuilder.DropTable(
                name: "ReportDefinitions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Users_ReportsWhatsAppOptIn",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ReportsWhatsAppNumber",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ReportsWhatsAppOptInAt",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ReportsWhatsAppOptOutAt",
                table: "Users");
        }
    }
}
