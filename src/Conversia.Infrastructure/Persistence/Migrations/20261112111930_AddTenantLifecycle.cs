using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Conversia.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "BillingHoldSince",
                table: "Tenants",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CancelledAt",
                table: "Tenants",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DataDeletionDueAt",
                table: "Tenants",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DataDeletionStartedAt",
                table: "Tenants",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StatusReasonCode",
                table: "Tenants",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SuspensionCause",
                table: "Tenants",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "AdminId",
                table: "PlatformAuditLogs",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "ReasonCode",
                table: "PlatformAuditLogs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ContactSuppressions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    WaIdHash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    HashKeyId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SuppressedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SuppressedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContactSuppressions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ContactSuppressions_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TenantDeletions",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    NameHash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    HashKeyId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CancelledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Trigger = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    RequestedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    StuckSince = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Counts = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantDeletions", x => x.TenantId);
                });

            // Every company suspended before this version was suspended by an administrator.
            migrationBuilder.Sql("""UPDATE "Tenants" SET "SuspensionCause" = 'Manual' WHERE "Status" = 'Suspended';""");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Tenants_Cancellation",
                table: "Tenants",
                sql: "(\"Status\" = 'Cancelled') = (\"CancelledAt\" IS NOT NULL AND \"DataDeletionDueAt\" IS NOT NULL) AND (\"DataDeletionStartedAt\" IS NULL OR \"Status\" = 'Cancelled')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Tenants_SuspensionCause",
                table: "Tenants",
                sql: "(\"Status\" = 'Suspended') = (\"SuspensionCause\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PlatformAuditLogs_SystemReasonCode",
                table: "PlatformAuditLogs",
                sql: "\"AdminId\" IS NOT NULL OR \"ReasonCode\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ContactSuppressions_TenantId_WaIdHash",
                table: "ContactSuppressions",
                columns: new[] { "TenantId", "WaIdHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantDeletions_CompletedAt",
                table: "TenantDeletions",
                column: "CompletedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ContactSuppressions");

            migrationBuilder.DropTable(
                name: "TenantDeletions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Tenants_Cancellation",
                table: "Tenants");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Tenants_SuspensionCause",
                table: "Tenants");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PlatformAuditLogs_SystemReasonCode",
                table: "PlatformAuditLogs");

            // The previous version knows Pending, Active and Suspended only: a paused or cancelled company goes back as suspended (it stops),
            // and a system audit entry gets the empty administrator id the previous schema requires.
            migrationBuilder.Sql("""UPDATE "Tenants" SET "Status" = 'Suspended' WHERE "Status" IN ('Paused', 'Cancelled');""");
            migrationBuilder.Sql("""UPDATE "PlatformAuditLogs" SET "AdminId" = '00000000-0000-0000-0000-000000000000' WHERE "AdminId" IS NULL;""");

            migrationBuilder.DropColumn(
                name: "BillingHoldSince",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "CancelledAt",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "DataDeletionDueAt",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "DataDeletionStartedAt",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "StatusReasonCode",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "SuspensionCause",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "ReasonCode",
                table: "PlatformAuditLogs");

            migrationBuilder.AlterColumn<Guid>(
                name: "AdminId",
                table: "PlatformAuditLogs",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
