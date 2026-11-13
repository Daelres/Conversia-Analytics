using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Conversia.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddNightlyAiAnalysis : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AiAnalysisIntervalHours",
                table: "Tenants",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AiAnalysisMode",
                table: "Tenants",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Nightly");

            migrationBuilder.AddColumn<int>(
                name: "AiAnalysisNightlyHour",
                table: "Tenants",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LlmAnalyzedAt",
                table: "Conversations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LlmAttemptedAt",
                table: "Conversations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LlmLeaseUntil",
                table: "Conversations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "LlmPending",
                table: "Conversations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "AiAnalysisRuns",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    WindowStart = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    WindowEnd = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Mode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastActivityAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Claimed = table.Column<int>(type: "integer", nullable: false),
                    Analysed = table.Column<int>(type: "integer", nullable: false),
                    Unchanged = table.Column<int>(type: "integer", nullable: false),
                    Skipped = table.Column<int>(type: "integer", nullable: false),
                    Discarded = table.Column<int>(type: "integer", nullable: false),
                    Failed = table.Column<int>(type: "integer", nullable: false),
                    Deferred = table.Column<int>(type: "integer", nullable: false),
                    Batched = table.Column<int>(type: "integer", nullable: false),
                    Calls = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiAnalysisRuns", x => new { x.TenantId, x.WindowStart });
                    table.ForeignKey(
                        name: "FK_AiAnalysisRuns_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LlmBatches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderBatchId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    RequestCount = table.Column<int>(type: "integer", nullable: false),
                    PreparedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SubmitStartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SubmittedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastPolledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EndedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CollectedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Succeeded = table.Column<int>(type: "integer", nullable: false),
                    Errored = table.Column<int>(type: "integer", nullable: false),
                    Expired = table.Column<int>(type: "integer", nullable: false),
                    Canceled = table.Column<int>(type: "integer", nullable: false),
                    Error = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LlmBatches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LlmBatchRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    WindowStart = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CustomId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SnapshotHash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    IsSandbox = table.Column<bool>(type: "boolean", nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    StageAtPlan = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    StageChangedAtPlan = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    LeaseUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EstimatedUsd = table.Column<decimal>(type: "numeric(12,6)", precision: 12, scale: 6, nullable: false),
                    CollectingAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LlmBatchRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LlmBatchRequests_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LlmBatchRequests_LlmBatches_BatchId",
                        column: x => x.BatchId,
                        principalTable: "LlmBatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LlmBatchRequests_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Tenants_AiAnalysisSchedule",
                table: "Tenants",
                sql: "(\"AiAnalysisMode\" = 'Nightly' AND \"AiAnalysisIntervalHours\" IS NULL AND (\"AiAnalysisNightlyHour\" IS NULL OR \"AiAnalysisNightlyHour\" BETWEEN 0 AND 23)) OR (\"AiAnalysisMode\" = 'EveryHours' AND \"AiAnalysisNightlyHour\" IS NULL AND \"AiAnalysisIntervalHours\" BETWEEN 1 AND 24) OR (\"AiAnalysisMode\" = 'NearRealTime' AND \"AiAnalysisNightlyHour\" IS NULL AND \"AiAnalysisIntervalHours\" IS NULL)");


            migrationBuilder.CreateIndex(
                name: "IX_AiAnalysisRuns_WindowStart",
                table: "AiAnalysisRuns",
                column: "WindowStart");

            migrationBuilder.CreateIndex(
                name: "IX_LlmBatches_Status_CreatedAt",
                table: "LlmBatches",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_LlmBatchRequests_BatchId_CustomId",
                table: "LlmBatchRequests",
                columns: new[] { "BatchId", "CustomId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LlmBatchRequests_ConversationId_CreatedAt",
                table: "LlmBatchRequests",
                columns: new[] { "ConversationId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_LlmBatchRequests_Status_TenantId",
                table: "LlmBatchRequests",
                columns: new[] { "Status", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_LlmBatchRequests_TenantId",
                table: "LlmBatchRequests",
                column: "TenantId");

            // Outside the schema transaction (it has committed by now), in batches of 10,000 rows with a commit each, so a large table is
            // never locked for long: conversations that were waiting for the LLM under the first stage (one flag for both) wait for it now,
            // with a request time (the claim orders by it). Conversations read by the rules only are not queued: the LLM reads a
            // conversation again when it receives a new message (docs/CLASSIFICATION.md, "When the AI reads").
            migrationBuilder.Sql(
                """
                DO $$
                DECLARE moved integer;
                BEGIN
                  LOOP
                    UPDATE "Conversations" SET "LlmPending" = true, "ClassificationRequestedAt" = COALESCE("ClassificationRequestedAt", "LastMessageAt")
                    WHERE "Id" IN (SELECT "Id" FROM "Conversations" WHERE "ClassificationDirty" AND NOT "LlmPending" LIMIT 10000);
                    GET DIAGNOSTICS moved = ROW_COUNT;
                    COMMIT;
                    EXIT WHEN moved = 0;
                  END LOOP;
                END $$;
                """,
                suppressTransaction: true);

            // Built CONCURRENTLY on an existing, possibly large table. A previous attempt that failed half-way leaves an INVALID index of the
            // same name: it is dropped first, so a rerun always ends with a valid one. It matches the claim: a company's pending
            // conversations, oldest request first.
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS \"IX_Conversations_LlmPending\";", suppressTransaction: true);
            migrationBuilder.Sql(
                "CREATE INDEX CONCURRENTLY \"IX_Conversations_LlmPending\" ON \"Conversations\" (\"TenantId\", \"ClassificationRequestedAt\") WHERE \"LlmPending\";",
                suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiAnalysisRuns");

            migrationBuilder.DropTable(
                name: "LlmBatchRequests");

            migrationBuilder.DropTable(
                name: "LlmBatches");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Tenants_AiAnalysisSchedule",
                table: "Tenants");

            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS \"IX_Conversations_LlmPending\";", suppressTransaction: true);

            migrationBuilder.DropColumn(
                name: "AiAnalysisIntervalHours",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "AiAnalysisMode",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "AiAnalysisNightlyHour",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "LlmAnalyzedAt",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "LlmAttemptedAt",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "LlmLeaseUntil",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "LlmPending",
                table: "Conversations");
        }
    }
}
