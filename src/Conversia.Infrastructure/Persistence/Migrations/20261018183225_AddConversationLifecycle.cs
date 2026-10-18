using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Conversia.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddConversationLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Opportunities_TenantId_Stage",
                table: "Opportunities");

            // The free-text objection and loss reason of the earlier model are replaced by enums; nothing ever wrote them, and a value that is
            // not a member name could not be read back. The two descriptive texts shrink, so they are cut to fit rather than failing the migration.
            migrationBuilder.Sql("UPDATE \"Opportunities\" SET \"Objection\" = NULL, \"LossReason\" = NULL;");
            migrationBuilder.Sql("UPDATE \"Opportunities\" SET \"ServiceInterest\" = LEFT(\"ServiceInterest\", 100), \"InterestReason\" = LEFT(\"InterestReason\", 200);");

            migrationBuilder.AlterColumn<string>(
                name: "ServiceInterest",
                table: "Opportunities",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Objection",
                table: "Opportunities",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "None",
                oldClrType: typeof(string),
                oldType: "character varying(500)",
                oldMaxLength: 500,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "LossReason",
                table: "Opportunities",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(500)",
                oldMaxLength: 500,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "InterestReason",
                table: "Opportunities",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(500)",
                oldMaxLength: 500,
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LossDetail",
                table: "Opportunities",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "StageChangedAt",
                table: "Opportunities",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StageSource",
                table: "Opportunities",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Auto");

            migrationBuilder.AddColumn<DateTime>(
                name: "ClosedAt",
                table: "Conversations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Opportunities_TenantId_Stage_ClosedAt",
                table: "Opportunities",
                columns: new[] { "TenantId", "Stage", "ClosedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_LastMessageAt_Open",
                table: "Conversations",
                column: "LastMessageAt",
                filter: "\"Status\" = 'Open'");

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_TenantId_StartedAt",
                table: "Conversations",
                columns: new[] { "TenantId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_TenantId_Status_LastMessageAt",
                table: "Conversations",
                columns: new[] { "TenantId", "Status", "LastMessageAt" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Conversations_ClosedAt",
                table: "Conversations",
                sql: "(\"Status\" = 'Closed') = (\"ClosedAt\" IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Opportunities_TenantId_Stage_ClosedAt",
                table: "Opportunities");

            migrationBuilder.DropIndex(
                name: "IX_Conversations_LastMessageAt_Open",
                table: "Conversations");

            migrationBuilder.DropIndex(
                name: "IX_Conversations_TenantId_StartedAt",
                table: "Conversations");

            migrationBuilder.DropIndex(
                name: "IX_Conversations_TenantId_Status_LastMessageAt",
                table: "Conversations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Conversations_ClosedAt",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "LossDetail",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "StageChangedAt",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "StageSource",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "ClosedAt",
                table: "Conversations");

            migrationBuilder.AlterColumn<string>(
                name: "ServiceInterest",
                table: "Opportunities",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Objection",
                table: "Opportunities",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20);

            migrationBuilder.AlterColumn<string>(
                name: "LossReason",
                table: "Opportunities",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "InterestReason",
                table: "Opportunities",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Opportunities_TenantId_Stage",
                table: "Opportunities",
                columns: new[] { "TenantId", "Stage" });
        }
    }
}
