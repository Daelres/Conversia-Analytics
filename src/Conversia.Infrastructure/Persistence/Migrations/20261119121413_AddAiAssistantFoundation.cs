using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Conversia.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAiAssistantFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AssistantEnabled",
                table: "WhatsAppChannels",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AssistantBlocked",
                table: "Tenants",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "AssistantActivityCounts",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    IsSandbox = table.Column<bool>(type: "boolean", nullable: false),
                    Metric = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantActivityCounts", x => new { x.TenantId, x.Day, x.IsSandbox, x.Metric });
                    table.ForeignKey(
                        name: "FK_AssistantActivityCounts_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AssistantKnowledgeVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Content = table.Column<string>(type: "jsonb", nullable: false),
                    PublishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PublishedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantKnowledgeVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssistantKnowledgeVersions_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AssistantSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    TermsVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    TermsAcceptedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TermsAcceptedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    ReplyDelaySeconds = table.Column<int>(type: "integer", nullable: false),
                    ActiveHours = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CustomStartMinute = table.Column<int>(type: "integer", nullable: true),
                    CustomEndMinute = table.Column<int>(type: "integer", nullable: true),
                    MaxAssistantTurns = table.Column<int>(type: "integer", nullable: false),
                    HumanCooldownMinutes = table.Column<int>(type: "integer", nullable: false),
                    DisclosureMode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    DisclosureText = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    HandoffLine = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    HandoffKeywords = table.Column<List<string>>(type: "text[]", nullable: false),
                    ToneNotes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Language = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantSettings", x => x.Id);
                    table.CheckConstraint("CK_AssistantSettings_Consent", "NOT \"Enabled\" OR (\"TermsVersion\" IS NOT NULL AND \"TermsAcceptedAt\" IS NOT NULL AND \"TermsAcceptedBy\" IS NOT NULL)");
                    table.CheckConstraint("CK_AssistantSettings_Cooldown", "\"HumanCooldownMinutes\" BETWEEN 0 AND 1440");
                    table.CheckConstraint("CK_AssistantSettings_ReplyDelay", "\"ReplyDelaySeconds\" BETWEEN 30 AND 900");
                    table.CheckConstraint("CK_AssistantSettings_Turns", "\"MaxAssistantTurns\" BETWEEN 1 AND 50");
                    table.ForeignKey(
                        name: "FK_AssistantSettings_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ConversationAssistants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsSandbox = table.Column<bool>(type: "boolean", nullable: false),
                    State = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    DueAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Generation = table.Column<int>(type: "integer", nullable: false),
                    ClaimedUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ArmedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AssistantTurns = table.Column<int>(type: "integer", nullable: false),
                    MisunderstandingCount = table.Column<int>(type: "integer", nullable: false),
                    HandoffReason = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    HandedOffAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    HumanTookOverAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    HumanHoldUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TakenOverBy = table.Column<Guid>(type: "uuid", nullable: true),
                    DisclosedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastRepliedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationAssistants", x => x.Id);
                    table.CheckConstraint("CK_ConversationAssistants_Timer", "(\"State\" = 'Waiting') = (\"DueAt\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_ConversationAssistants_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConversationAssistants_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PlatformSwitches",
                columns: table => new
                {
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    ChangedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ChangedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformSwitches", x => x.Name);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantActivityCounts_Day",
                table: "AssistantActivityCounts",
                column: "Day");

            migrationBuilder.CreateIndex(
                name: "IX_AssistantKnowledgeVersions_Published",
                table: "AssistantKnowledgeVersions",
                column: "TenantId",
                unique: true,
                filter: "\"Status\" = 'Published'");

            migrationBuilder.CreateIndex(
                name: "IX_AssistantKnowledgeVersions_TenantId_Number",
                table: "AssistantKnowledgeVersions",
                columns: new[] { "TenantId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssistantSettings_TenantId",
                table: "AssistantSettings",
                column: "TenantId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConversationAssistants_ConversationId",
                table: "ConversationAssistants",
                column: "ConversationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConversationAssistants_Due",
                table: "ConversationAssistants",
                column: "DueAt",
                filter: "\"State\" = 'Waiting'");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationAssistants_TenantId",
                table: "ConversationAssistants",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssistantActivityCounts");

            migrationBuilder.DropTable(
                name: "AssistantKnowledgeVersions");

            migrationBuilder.DropTable(
                name: "AssistantSettings");

            migrationBuilder.DropTable(
                name: "ConversationAssistants");

            migrationBuilder.DropTable(
                name: "PlatformSwitches");

            migrationBuilder.DropColumn(
                name: "AssistantEnabled",
                table: "WhatsAppChannels");

            migrationBuilder.DropColumn(
                name: "AssistantBlocked",
                table: "Tenants");
        }
    }
}
