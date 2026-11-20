using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Conversia.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddClosingPointAndAnalysisCapture : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ClosedBy",
                table: "Opportunities",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.AddColumn<string>(
                name: "ClosedByClassifiedBy",
                table: "Opportunities",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClosedBySource",
                table: "Opportunities",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Auto");

            migrationBuilder.AddColumn<bool>(
                name: "ClosedOutsideSystem",
                table: "Opportunities",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ClosedOutsideSystemClassifiedBy",
                table: "Opportunities",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClosedOutsideSystemSource",
                table: "Opportunities",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Auto");

            migrationBuilder.AddColumn<string>(
                name: "MovedOffChannel",
                table: "Opportunities",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "None");

            migrationBuilder.AddColumn<string>(
                name: "MovedOffChannelClassifiedBy",
                table: "Opportunities",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MovedOffChannelSource",
                table: "Opportunities",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Auto");

            migrationBuilder.AddColumn<int>(
                name: "OffChannelEvidenceIndex",
                table: "Opportunities",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReachedStage",
                table: "Opportunities",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReachedStageClassifiedBy",
                table: "Opportunities",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReachedStageSource",
                table: "Opportunities",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Auto");

            migrationBuilder.AddColumn<Guid>(
                name: "AssignedAdvisorIdAtReceipt",
                table: "Messages",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "HasCaption",
                table: "Messages",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "MediaId",
                table: "Messages",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MediaMimeType",
                table: "Messages",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Sender",
                table: "Messages",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.AddColumn<Guid>(
                name: "SentByAdvisorId",
                table: "Messages",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SentByUserId",
                table: "Messages",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReferralHeadline",
                table: "Conversations",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReferralSourceId",
                table: "Conversations",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReferralSourceType",
                table: "Conversations",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReferralSourceUrl",
                table: "Conversations",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            // Outside the schema transaction (it has committed by now), in batches of 10,000 rows with a commit each, so a large table is
            // never locked for long (the pattern of AddNightlyAiAnalysis). Every stored inbound message was the customer's; outbound ones
            // stay Unknown (who sent them was not recorded before).
            migrationBuilder.Sql(
                """
                DO $$
                DECLARE moved integer;
                BEGIN
                  LOOP
                    UPDATE "Messages" SET "Sender" = 'Customer'
                    WHERE "Id" IN (SELECT "Id" FROM "Messages" WHERE "Direction" = 'Inbound' AND "Sender" <> 'Customer' LIMIT 10000);
                    GET DIAGNOSTICS moved = ROW_COUNT;
                    COMMIT;
                    EXIT WHEN moved = 0;
                  END LOOP;
                END $$;
                """,
                suppressTransaction: true);

            // The rules read the closing point of the history on their next pass (cheap, near real time): a one-off backlog of every
            // conversation with a customer message (docs/CLASSIFICATION.md, "Closing point", upgrade note). A conversation waiting for
            // the LLM is left exactly as it is: changing its request time would make phase 3 discard an answer that may already be paid
            // for (a submitted batch); it gets its closing point from the LLM, or from the rules at its next message. Conversations already
            // flagged keep their request.
            migrationBuilder.Sql(
                """
                DO $$
                DECLARE moved integer;
                BEGIN
                  LOOP
                    UPDATE "Conversations" SET "ClassificationDirty" = true, "ClassificationFailures" = 0, "ClassificationRequestedAt" = now()
                    WHERE "Id" IN (SELECT "Id" FROM "Conversations"
                                   WHERE "FirstInboundAt" IS NOT NULL AND NOT "ClassificationDirty" AND NOT "LlmPending" LIMIT 10000);
                    GET DIAGNOSTICS moved = ROW_COUNT;
                    COMMIT;
                    EXIT WHEN moved = 0;
                  END LOOP;
                END $$;
                """,
                suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClosedBy",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "ClosedByClassifiedBy",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "ClosedBySource",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "ClosedOutsideSystem",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "ClosedOutsideSystemClassifiedBy",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "ClosedOutsideSystemSource",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "MovedOffChannel",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "MovedOffChannelClassifiedBy",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "MovedOffChannelSource",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "OffChannelEvidenceIndex",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "ReachedStage",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "ReachedStageClassifiedBy",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "ReachedStageSource",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "AssignedAdvisorIdAtReceipt",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "HasCaption",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "MediaId",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "MediaMimeType",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "Sender",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "SentByAdvisorId",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "SentByUserId",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "ReferralHeadline",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "ReferralSourceId",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "ReferralSourceType",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "ReferralSourceUrl",
                table: "Conversations");
        }
    }
}
