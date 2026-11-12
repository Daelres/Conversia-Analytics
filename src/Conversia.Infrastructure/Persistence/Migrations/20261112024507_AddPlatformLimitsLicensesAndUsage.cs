using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Conversia.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPlatformLimitsLicensesAndUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AiEnabled",
                table: "Tenants",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "AiEnabledChangedAt",
                table: "Tenants",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AiMonthlyConversationQuota",
                table: "Tenants",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AiTermsVersion",
                table: "Tenants",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SendRateLimitPerMinute",
                table: "Tenants",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AiUsageConversations",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Month = table.Column<DateOnly>(type: "date", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiUsageConversations", x => new { x.TenantId, x.Month, x.ConversationId });
                    table.ForeignKey(
                        name: "FK_AiUsageConversations_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AiUsageMonths",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Month = table.Column<DateOnly>(type: "date", nullable: false),
                    IsSandbox = table.Column<bool>(type: "boolean", nullable: false),
                    ConversationsAnalyzed = table.Column<int>(type: "integer", nullable: false),
                    Calls = table.Column<int>(type: "integer", nullable: false),
                    FailedCalls = table.Column<int>(type: "integer", nullable: false),
                    InputTokens = table.Column<long>(type: "bigint", nullable: false),
                    OutputTokens = table.Column<long>(type: "bigint", nullable: false),
                    CacheReadTokens = table.Column<long>(type: "bigint", nullable: false),
                    CacheWriteTokens = table.Column<long>(type: "bigint", nullable: false),
                    EstimatedUsd = table.Column<decimal>(type: "numeric(12,6)", precision: 12, scale: 6, nullable: false),
                    WonDisagreements = table.Column<int>(type: "integer", nullable: false),
                    ExhaustedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UsdCeilingReachedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiUsageMonths", x => new { x.TenantId, x.Month, x.IsSandbox });
                    table.ForeignKey(
                        name: "FK_AiUsageMonths_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LineLicenses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PhoneDigits = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: false),
                    ProviderChannelId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    MonthlyCostUsd = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    CustomerOwned = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AssignedTenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    ChannelId = table.Column<Guid>(type: "uuid", nullable: true),
                    AssignedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReleasedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RetiredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LineLicenses", x => x.Id);
                    table.CheckConstraint("CK_LineLicenses_Assignment", "(\"Status\" = 'Assigned') = (\"AssignedTenantId\" IS NOT NULL)");
                    table.CheckConstraint("CK_LineLicenses_CustomerOwnedCost", "NOT \"CustomerOwned\" OR \"MonthlyCostUsd\" = 0");
                    table.CheckConstraint("CK_LineLicenses_MonthlyCostUsd", "\"MonthlyCostUsd\" BETWEEN 0 AND 10000");
                });

            migrationBuilder.CreateTable(
                name: "MessageSendOutcomes",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    IsSandbox = table.Column<bool>(type: "boolean", nullable: false),
                    Outcome = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageSendOutcomes", x => new { x.TenantId, x.Day, x.IsSandbox, x.Outcome });
                    table.ForeignKey(
                        name: "FK_MessageSendOutcomes_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Tenants_AiMonthlyConversationQuota",
                table: "Tenants",
                sql: "\"AiMonthlyConversationQuota\" IS NULL OR \"AiMonthlyConversationQuota\" BETWEEN 0 AND 1000000");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Tenants_SendRateLimitPerMinute",
                table: "Tenants",
                sql: "\"SendRateLimitPerMinute\" IS NULL OR \"SendRateLimitPerMinute\" BETWEEN 1 AND 1000000");

            migrationBuilder.CreateIndex(
                name: "IX_AiUsageConversations_Month",
                table: "AiUsageConversations",
                column: "Month");

            migrationBuilder.CreateIndex(
                name: "IX_AiUsageMonths_Month",
                table: "AiUsageMonths",
                column: "Month");

            migrationBuilder.CreateIndex(
                name: "IX_LineLicenses_AssignedTenantId_Status",
                table: "LineLicenses",
                columns: new[] { "AssignedTenantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_LineLicenses_PhoneDigits",
                table: "LineLicenses",
                column: "PhoneDigits",
                unique: true,
                filter: "\"Status\" <> 'Retired'");

            migrationBuilder.CreateIndex(
                name: "IX_LineLicenses_Status_CreatedAt",
                table: "LineLicenses",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MessageSendOutcomes_Day",
                table: "MessageSendOutcomes",
                column: "Day");

            // The two indexes on existing, possibly large tables are built CONCURRENTLY, outside the migration's transaction, so webhooks and
            // conversations keep being written while they build (a plain CREATE INDEX would block writes on them for the whole build).
            // IF NOT EXISTS makes a re-run after an interrupted build safe; an interrupted concurrent build can leave an INVALID index, which
            // docs/DOCKER.md ("Upgrade notes") says how to drop and rebuild. The model snapshot declares both indexes as usual.
            migrationBuilder.Sql(
                "CREATE INDEX CONCURRENTLY IF NOT EXISTS \"IX_WebhookEvents_ReceivedAt\" ON \"WebhookEvents\" (\"ReceivedAt\");",
                suppressTransaction: true);
            migrationBuilder.Sql(
                "CREATE INDEX CONCURRENTLY IF NOT EXISTS \"IX_Conversations_TenantId_ClosedAt\" ON \"Conversations\" (\"TenantId\", \"ClosedAt\");",
                suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS \"IX_WebhookEvents_ReceivedAt\";", suppressTransaction: true);
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS \"IX_Conversations_TenantId_ClosedAt\";", suppressTransaction: true);

            migrationBuilder.DropTable(
                name: "AiUsageConversations");

            migrationBuilder.DropTable(
                name: "AiUsageMonths");

            migrationBuilder.DropTable(
                name: "LineLicenses");

            migrationBuilder.DropTable(
                name: "MessageSendOutcomes");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Tenants_AiMonthlyConversationQuota",
                table: "Tenants");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Tenants_SendRateLimitPerMinute",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "AiEnabled",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "AiEnabledChangedAt",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "AiMonthlyConversationQuota",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "AiTermsVersion",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "SendRateLimitPerMinute",
                table: "Tenants");
        }
    }
}
