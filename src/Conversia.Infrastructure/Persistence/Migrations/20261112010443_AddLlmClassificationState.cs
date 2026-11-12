using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Conversia.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLlmClassificationState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastLlmSnapshotHash",
                table: "Conversations",
                type: "character(64)",
                fixedLength: true,
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PendingAgeRange",
                table: "Conversations",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PendingGender",
                table: "Conversations",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ProfilePending",
                table: "Conversations",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastLlmSnapshotHash",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "PendingAgeRange",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "PendingGender",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "ProfilePending",
                table: "Conversations");
        }
    }
}
