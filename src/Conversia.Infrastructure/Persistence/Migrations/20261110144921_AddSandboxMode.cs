using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Conversia.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSandboxMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsSandbox",
                table: "WebhookEvents",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "SandboxMode",
                table: "Tenants",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsSandbox",
                table: "Conversations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsSandbox",
                table: "Contacts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_TenantId_Sandbox",
                table: "Conversations",
                column: "TenantId",
                filter: "\"IsSandbox\"");
        }

        /// <inheritdoc />
        /// <remarks>
        /// Rolling this back drops the sandbox marks: sandbox contacts, conversations and deliveries then become indistinguishable from real
        /// ones and enter every metric. Delete the sandbox data of every company first (<c>DELETE /api/sandbox/data</c>, docs/SANDBOX.md).
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // See the remarks: delete the sandbox data before running this, or it is counted as real from then on.
            migrationBuilder.DropIndex(
                name: "IX_Conversations_TenantId_Sandbox",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "IsSandbox",
                table: "WebhookEvents");

            migrationBuilder.DropColumn(
                name: "SandboxMode",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "IsSandbox",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "IsSandbox",
                table: "Contacts");
        }
    }
}
