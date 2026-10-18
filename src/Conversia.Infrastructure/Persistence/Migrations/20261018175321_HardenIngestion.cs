using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Conversia.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HardenIngestion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SkippedMessageCount",
                table: "WebhookEvents",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StatusCount",
                table: "WebhookEvents",
                type: "integer",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "WaId",
                table: "Contacts",
                type: "character varying(15)",
                maxLength: 15,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20);

            migrationBuilder.AddColumn<DateTime>(
                name: "DisplayNameAt",
                table: "Contacts",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SkippedMessageCount",
                table: "WebhookEvents");

            migrationBuilder.DropColumn(
                name: "StatusCount",
                table: "WebhookEvents");

            migrationBuilder.DropColumn(
                name: "DisplayNameAt",
                table: "Contacts");

            migrationBuilder.AlterColumn<string>(
                name: "WaId",
                table: "Contacts",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(15)",
                oldMaxLength: 15);
        }
    }
}
