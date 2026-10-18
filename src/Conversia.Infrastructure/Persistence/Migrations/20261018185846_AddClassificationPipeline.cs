using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Conversia.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddClassificationPipeline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "ClassificationConfidence",
                table: "Opportunities",
                type: "numeric(3,2)",
                precision: 3,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClassificationRules",
                table: "Opportunities",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ClassificationAttemptedAt",
                table: "Conversations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ClassificationDirty",
                table: "Conversations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "ClassificationFailures",
                table: "Conversations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "ClassificationRequestedAt",
                table: "Conversations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_LastMessageAt_ClassificationDirty",
                table: "Conversations",
                column: "LastMessageAt",
                filter: "\"ClassificationDirty\"");

            // The conversations stored before the classifier existed are queued, so its first runs read the history.
            migrationBuilder.Sql("UPDATE \"Conversations\" SET \"ClassificationDirty\" = true, \"ClassificationRequestedAt\" = now() WHERE \"FirstInboundAt\" IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Conversations_LastMessageAt_ClassificationDirty",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "ClassificationConfidence",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "ClassificationRules",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "ClassificationAttemptedAt",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "ClassificationDirty",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "ClassificationFailures",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "ClassificationRequestedAt",
                table: "Conversations");
        }
    }
}
