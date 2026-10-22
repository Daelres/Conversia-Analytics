using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Conversia.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWebhookEventMessageCounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WebhookEvents_TenantId",
                table: "WebhookEvents");

            migrationBuilder.AddColumn<int>(
                name: "DuplicateMessageCount",
                table: "WebhookEvents",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StoredMessageCount",
                table: "WebhookEvents",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_WebhookEvents_TenantId_ReceivedAt",
                table: "WebhookEvents",
                columns: new[] { "TenantId", "ReceivedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WebhookEvents_TenantId_ReceivedAt",
                table: "WebhookEvents");

            migrationBuilder.DropColumn(
                name: "DuplicateMessageCount",
                table: "WebhookEvents");

            migrationBuilder.DropColumn(
                name: "StoredMessageCount",
                table: "WebhookEvents");

            migrationBuilder.CreateIndex(
                name: "IX_WebhookEvents_TenantId",
                table: "WebhookEvents",
                column: "TenantId");
        }
    }
}
