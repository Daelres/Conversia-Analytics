using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Conversia.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddExtractionProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ClassifiedBy",
                table: "Opportunities",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DetailsChangedAt",
                table: "Opportunities",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DetailsChangedBy",
                table: "Opportunities",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InterestReasonClassifiedBy",
                table: "Opportunities",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InterestReasonSource",
                table: "Opportunities",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Auto");

            migrationBuilder.AddColumn<string>(
                name: "ObjectionClassifiedBy",
                table: "Opportunities",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ObjectionSource",
                table: "Opportunities",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Auto");

            migrationBuilder.AddColumn<string>(
                name: "ServiceInterestClassifiedBy",
                table: "Opportunities",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ServiceInterestSource",
                table: "Opportunities",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Auto");

            migrationBuilder.AddColumn<int>(
                name: "LlmFailures",
                table: "Conversations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "AgeRange",
                table: "Contacts",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AgeRangeSource",
                table: "Contacts",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Gender",
                table: "Contacts",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Undetermined");

            migrationBuilder.AddColumn<string>(
                name: "GenderSource",
                table: "Contacts",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ProfileChangedAt",
                table: "Contacts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ProfileChangedBy",
                table: "Contacts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProfileClassifiedBy",
                table: "Contacts",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);
        }

        /// <inheritdoc />
        /// <remarks>
        /// Forward-only once released: rolling back drops the per-field provenance, so every value a person set (or pinned) becomes automatic
        /// again and the next classification may overwrite it, and the extracted age range and gender of the contacts are lost.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClassifiedBy",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "DetailsChangedAt",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "DetailsChangedBy",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "InterestReasonClassifiedBy",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "InterestReasonSource",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "ObjectionClassifiedBy",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "ObjectionSource",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "ServiceInterestClassifiedBy",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "ServiceInterestSource",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "LlmFailures",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "AgeRange",
                table: "Contacts");

            migrationBuilder.DropColumn(
                name: "AgeRangeSource",
                table: "Contacts");

            migrationBuilder.DropColumn(
                name: "Gender",
                table: "Contacts");

            migrationBuilder.DropColumn(
                name: "GenderSource",
                table: "Contacts");

            migrationBuilder.DropColumn(
                name: "ProfileChangedAt",
                table: "Contacts");

            migrationBuilder.DropColumn(
                name: "ProfileChangedBy",
                table: "Contacts");

            migrationBuilder.DropColumn(
                name: "ProfileClassifiedBy",
                table: "Contacts");
        }
    }
}
