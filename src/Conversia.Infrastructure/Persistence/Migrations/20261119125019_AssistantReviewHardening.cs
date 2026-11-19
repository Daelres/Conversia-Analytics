using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Conversia.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AssistantReviewHardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Attempts",
                table: "ConversationAssistants",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "SendingStartedAt",
                table: "ConversationAssistants",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SendingTextHash",
                table: "ConversationAssistants",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AssistantAuditEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Details = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantAuditEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssistantAuditEntries_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantAuditEntries_TenantId_At",
                table: "AssistantAuditEntries",
                columns: new[] { "TenantId", "At" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssistantAuditEntries");

            migrationBuilder.DropColumn(
                name: "Attempts",
                table: "ConversationAssistants");

            migrationBuilder.DropColumn(
                name: "SendingStartedAt",
                table: "ConversationAssistants");

            migrationBuilder.DropColumn(
                name: "SendingTextHash",
                table: "ConversationAssistants");
        }
    }
}
