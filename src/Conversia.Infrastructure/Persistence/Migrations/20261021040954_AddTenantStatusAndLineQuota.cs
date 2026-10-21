using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Conversia.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantStatusAndLineQuota : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MaxActiveLines",
                table: "Tenants",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "Tenants",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Active");

            migrationBuilder.AddColumn<DateTime>(
                name: "StatusChangedAt",
                table: "Tenants",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "StatusChangedBy",
                table: "Tenants",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StatusReason",
                table: "Tenants",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Tenants_MaxActiveLines",
                table: "Tenants",
                sql: "\"MaxActiveLines\" BETWEEN 0 AND 1000");

            // The companies that exist today were operating before there was an approval step, so they stay operating: every existing
            // tenant becomes Active (the column default above did it) with a quota of the lines it has switched on now, and at least 4,
            // the four branches of the pilot customer. Channels are not touched: each keeps its IsActive. The quota never leaves a
            // company with more active lines than it may have. StatusChangedBy stays null: no administrator did this.
            migrationBuilder.Sql("""
                UPDATE "Tenants" t
                SET "MaxActiveLines" = GREATEST(4, (SELECT COUNT(*) FROM "WhatsAppChannels" c WHERE c."TenantId" = t."Id" AND c."IsActive")),
                    "StatusChangedAt" = now(),
                    "StatusReason" = 'Existing company when the approval step was introduced.';
                """);

            // From now on a company is Pending unless the code says otherwise: no database default for the status.
            migrationBuilder.AlterColumn<string>(
                name: "Status",
                table: "Tenants",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20,
                oldDefaultValue: "Active");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Tenants_MaxActiveLines",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "MaxActiveLines",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "StatusChangedAt",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "StatusChangedBy",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "StatusReason",
                table: "Tenants");
        }
    }
}
