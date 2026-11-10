using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Conversia.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddChannelProviderCredentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProviderApiKeyCiphertext",
                table: "WhatsAppChannels",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ProviderApiKeySetAt",
                table: "WhatsAppChannels",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderEnvironment",
                table: "WhatsAppChannels",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Sandbox");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProviderApiKeyCiphertext",
                table: "WhatsAppChannels");

            migrationBuilder.DropColumn(
                name: "ProviderApiKeySetAt",
                table: "WhatsAppChannels");

            migrationBuilder.DropColumn(
                name: "ProviderEnvironment",
                table: "WhatsAppChannels");
        }
    }
}
