using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Conversia.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInboxTemplatesAndReadReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ReadReceiptsEnabled",
                table: "Tenants",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "TemplateCategory",
                table: "Messages",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TemplateName",
                table: "Messages",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CustomerReadMarkedAt",
                table: "Conversations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastInboundAt",
                table: "Conversations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastOutboundAt",
                table: "Conversations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MessageTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChannelId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Language = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: false),
                    Category = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    BodyText = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    ParameterNames = table.Column<string[]>(type: "text[]", nullable: false),
                    IsSupported = table.Column<bool>(type: "boolean", nullable: false),
                    SyncedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StatusChangedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageTemplates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MessageTemplates_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MessageTemplates_WhatsAppChannels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "WhatsAppChannels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MessageTemplates_ChannelId_Name_Language",
                table: "MessageTemplates",
                columns: new[] { "ChannelId", "Name", "Language" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MessageTemplates_TenantId",
                table: "MessageTemplates",
                column: "TenantId");

            // Outside the schema transaction (it has committed by now), in batches of 10,000 rows with a commit each, so a large table is
            // never locked for long (the pattern of AddClosingPointAndAnalysisCapture). The last message of each side, as ConversationTimeline
            // computes it (docs/MESSAGING.md, "Customer service window"), and a change time for every conversation (the inbox's token and
            // delta read it without COALESCE; new rows get one when they are created).
            migrationBuilder.Sql(
                """
                DO $$
                DECLARE last_id uuid := '00000000-0000-0000-0000-000000000000';
                DECLARE batch_end uuid;
                BEGIN
                  -- Keyset over the ids: every row is visited once, whatever its data, so the loop always ends.
                  LOOP
                    SELECT "Id" INTO batch_end FROM "Conversations" WHERE "Id" > last_id ORDER BY "Id" OFFSET 9999 LIMIT 1;
                    IF batch_end IS NULL THEN
                      SELECT "Id" INTO batch_end FROM "Conversations" WHERE "Id" > last_id ORDER BY "Id" DESC LIMIT 1;
                    END IF;
                    EXIT WHEN batch_end IS NULL;
                    UPDATE "Conversations" c
                    SET "LastInboundAt" = (SELECT max(m."SentAt") FROM "Messages" m WHERE m."ConversationId" = c."Id" AND m."Direction" = 'Inbound'),
                        "LastOutboundAt" = (SELECT max(m."SentAt") FROM "Messages" m WHERE m."ConversationId" = c."Id" AND m."Direction" = 'Outbound'),
                        "UpdatedAt" = COALESCE(c."UpdatedAt", c."CreatedAt")
                    WHERE c."Id" > last_id AND c."Id" <= batch_end;
                    last_id := batch_end;
                    COMMIT;
                  END LOOP;
                END $$;
                """,
                suppressTransaction: true);

            // Built CONCURRENTLY on an existing, possibly large table. A previous attempt that failed half-way leaves an INVALID index of the
            // same name: it is dropped first, so a rerun always ends with a valid one.
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS \"IX_Conversations_Inbox_Changes\";", suppressTransaction: true);
            migrationBuilder.Sql(
                "CREATE INDEX CONCURRENTLY \"IX_Conversations_Inbox_Changes\" ON \"Conversations\" (\"TenantId\", \"IsSandbox\", \"UpdatedAt\");",
                suppressTransaction: true);
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS \"IX_Conversations_Inbox_Open\";", suppressTransaction: true);
            migrationBuilder.Sql(
                "CREATE INDEX CONCURRENTLY \"IX_Conversations_Inbox_Open\" ON \"Conversations\" (\"TenantId\", \"IsSandbox\", \"LastMessageAt\") WHERE \"Status\" = 'Open';",
                suppressTransaction: true);
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS \"IX_Conversations_Inbox_Waiting\";", suppressTransaction: true);
            migrationBuilder.Sql(
                "CREATE INDEX CONCURRENTLY \"IX_Conversations_Inbox_Waiting\" ON \"Conversations\" (\"TenantId\", \"IsSandbox\", \"LastInboundAt\") WHERE \"Status\" = 'Open';",
                suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MessageTemplates");

            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS \"IX_Conversations_Inbox_Changes\";", suppressTransaction: true);

            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS \"IX_Conversations_Inbox_Open\";", suppressTransaction: true);

            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS \"IX_Conversations_Inbox_Waiting\";", suppressTransaction: true);

            migrationBuilder.DropColumn(
                name: "ReadReceiptsEnabled",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "TemplateCategory",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "TemplateName",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "CustomerReadMarkedAt",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "LastInboundAt",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "LastOutboundAt",
                table: "Conversations");
        }
    }
}
