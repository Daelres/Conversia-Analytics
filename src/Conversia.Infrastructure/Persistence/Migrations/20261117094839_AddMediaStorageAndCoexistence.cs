using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Conversia.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMediaStorageAndCoexistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "HistoryAiAnalysisEnabled",
                table: "WhatsAppChannels",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "HistoryImportConsentedAt",
                table: "WhatsAppChannels",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "HistoryImportConsentedBy",
                table: "WhatsAppChannels",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "HistoryImportEnabled",
                table: "WhatsAppChannels",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "ProviderAccountEventAt",
                table: "WhatsAppChannels",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ProviderDisconnectedAt",
                table: "WhatsAppChannels",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderDisconnectionEvent",
                table: "WhatsAppChannels",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderDisconnectionReason",
                table: "WhatsAppChannels",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MediaRetentionDays",
                table: "Tenants",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsImported",
                table: "Messages",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "MediaAttempts",
                table: "Messages",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "MediaContentType",
                table: "Messages",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "MediaNextAttemptAt",
                table: "Messages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MediaObjectKey",
                table: "Messages",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "MediaSizeBytes",
                table: "Messages",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MediaStatus",
                table: "Messages",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "MediaStoredAt",
                table: "Messages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsImported",
                table: "Conversations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "MediaObjectDeletions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ObjectKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    NextAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaObjectDeletions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MediaObjectDeletions_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Tenants_MediaRetentionDays",
                table: "Tenants",
                sql: "\"MediaRetentionDays\" IS NULL OR \"MediaRetentionDays\" BETWEEN 1 AND 3650");

            migrationBuilder.CreateIndex(
                name: "IX_MediaObjectDeletions_TenantId_CreatedAt",
                table: "MediaObjectDeletions",
                columns: new[] { "TenantId", "CreatedAt" });

            // Media received before this stage kept only its provider id (docs/WEBHOOKS.md, "Media metadata"). What is still within the 7 days
            // Meta keeps received media waits for the download job; anything older can no longer be fetched. Only rows with a media id are
            // touched (a small share of the messages).
            migrationBuilder.Sql("""
                UPDATE "Messages"
                SET "MediaStatus" = CASE WHEN "CreatedAt" > now() - interval '6 days' THEN 'Pending' ELSE 'Expired' END,
                    "MediaNextAttemptAt" = CASE WHEN "CreatedAt" > now() - interval '6 days' THEN now() ELSE NULL END
                WHERE "MediaId" IS NOT NULL AND "MediaStatus" IS NULL
                """);

            // Built CONCURRENTLY on an existing, possibly large table (the same pattern as AddInboxTemplatesAndReadReceipts): a previous attempt
            // that failed half way leaves an INVALID index, dropped first.
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS \"IX_Messages_MediaPending\";", suppressTransaction: true);
            migrationBuilder.Sql(
                "CREATE INDEX CONCURRENTLY \"IX_Messages_MediaPending\" ON \"Messages\" (\"MediaNextAttemptAt\") WHERE \"MediaStatus\" = 'Pending';",
                suppressTransaction: true);
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS \"IX_Messages_MediaStored\";", suppressTransaction: true);
            migrationBuilder.Sql(
                "CREATE INDEX CONCURRENTLY \"IX_Messages_MediaStored\" ON \"Messages\" (\"TenantId\", \"MediaStoredAt\") WHERE \"MediaStatus\" = 'Stored';",
                suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MediaObjectDeletions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Tenants_MediaRetentionDays",
                table: "Tenants");

            migrationBuilder.DropIndex(
                name: "IX_Messages_MediaPending",
                table: "Messages");

            migrationBuilder.DropIndex(
                name: "IX_Messages_MediaStored",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "HistoryAiAnalysisEnabled",
                table: "WhatsAppChannels");

            migrationBuilder.DropColumn(
                name: "HistoryImportConsentedAt",
                table: "WhatsAppChannels");

            migrationBuilder.DropColumn(
                name: "HistoryImportConsentedBy",
                table: "WhatsAppChannels");

            migrationBuilder.DropColumn(
                name: "HistoryImportEnabled",
                table: "WhatsAppChannels");

            migrationBuilder.DropColumn(
                name: "ProviderAccountEventAt",
                table: "WhatsAppChannels");

            migrationBuilder.DropColumn(
                name: "ProviderDisconnectedAt",
                table: "WhatsAppChannels");

            migrationBuilder.DropColumn(
                name: "ProviderDisconnectionEvent",
                table: "WhatsAppChannels");

            migrationBuilder.DropColumn(
                name: "ProviderDisconnectionReason",
                table: "WhatsAppChannels");

            migrationBuilder.DropColumn(
                name: "MediaRetentionDays",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "IsImported",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "MediaAttempts",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "MediaContentType",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "MediaNextAttemptAt",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "MediaObjectKey",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "MediaSizeBytes",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "MediaStatus",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "MediaStoredAt",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "IsImported",
                table: "Conversations");
        }
    }
}
