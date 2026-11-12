using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Conversia.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSubscriptionBilling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BillingEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BodyHash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    EventType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TransactionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PaymentLinkId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ReceivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ProcessedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BillingEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BillingPlans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    PricePerLineInCents = table.Column<long>(type: "bigint", nullable: false),
                    IncludedAiConversations = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BillingPlans", x => x.Id);
                    table.CheckConstraint("CK_BillingPlans_IncludedAi", "\"IncludedAiConversations\" BETWEEN 0 AND 1000000");
                    table.CheckConstraint("CK_BillingPlans_Price", "\"PricePerLineInCents\" BETWEEN 0 AND 10000000000");
                });

            migrationBuilder.CreateTable(
                name: "BillingReviewItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    ChargeId = table.Column<Guid>(type: "uuid", nullable: true),
                    AttemptId = table.Column<Guid>(type: "uuid", nullable: true),
                    TransactionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Reason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AmountInCents = table.Column<long>(type: "bigint", nullable: true),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    FlaggedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReviewedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReviewedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    ReviewNote = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BillingReviewItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BillingAddOns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PlanId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Conversations = table.Column<int>(type: "integer", nullable: false),
                    PriceInCents = table.Column<long>(type: "bigint", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BillingAddOns", x => x.Id);
                    table.CheckConstraint("CK_BillingAddOns_Conversations", "\"Conversations\" BETWEEN 1 AND 1000000");
                    table.CheckConstraint("CK_BillingAddOns_Price", "\"PriceInCents\" BETWEEN 0 AND 10000000000");
                    table.ForeignKey(
                        name: "FK_BillingAddOns_BillingPlans_PlanId",
                        column: x => x.PlanId,
                        principalTable: "BillingPlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BillingSubscriptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlanId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AnchorDay = table.Column<int>(type: "integer", nullable: false),
                    CurrentPeriodStart = table.Column<DateOnly>(type: "date", nullable: false),
                    CurrentPeriodEnd = table.Column<DateOnly>(type: "date", nullable: false),
                    BilledLinesOverride = table.Column<int>(type: "integer", nullable: true),
                    AddOnId = table.Column<Guid>(type: "uuid", nullable: true),
                    AddOnQuantity = table.Column<int>(type: "integer", nullable: false),
                    CancelAtPeriodEnd = table.Column<bool>(type: "boolean", nullable: false),
                    CancelledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CancellationReasonCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PaymentSourceId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PaymentMethodType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    PaymentMethodLabel = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    BillingEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    PaymentMethodUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    BillingSuspendedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DunningRestartedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    NextRunAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BillingSubscriptions", x => x.Id);
                    table.CheckConstraint("CK_BillingSubscriptions_AddOn", "(\"AddOnId\" IS NULL AND \"AddOnQuantity\" = 0) OR (\"AddOnId\" IS NOT NULL AND \"AddOnQuantity\" BETWEEN 1 AND 20)");
                    table.CheckConstraint("CK_BillingSubscriptions_AnchorDay", "\"AnchorDay\" BETWEEN 1 AND 31");
                    table.CheckConstraint("CK_BillingSubscriptions_BilledLines", "\"BilledLinesOverride\" IS NULL OR \"BilledLinesOverride\" BETWEEN 1 AND 1000");
                    table.CheckConstraint("CK_BillingSubscriptions_PaymentMethod", "(\"PaymentSourceId\" IS NULL) = (\"PaymentMethodType\" IS NULL)");
                    table.CheckConstraint("CK_BillingSubscriptions_Period", "\"CurrentPeriodEnd\" >= \"CurrentPeriodStart\"");
                    table.ForeignKey(
                        name: "FK_BillingSubscriptions_BillingAddOns_AddOnId",
                        column: x => x.AddOnId,
                        principalTable: "BillingAddOns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BillingSubscriptions_BillingPlans_PlanId",
                        column: x => x.PlanId,
                        principalTable: "BillingPlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BillingSubscriptions_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BillingCharges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubscriptionId = table.Column<Guid>(type: "uuid", nullable: false),
                    PeriodStart = table.Column<DateOnly>(type: "date", nullable: false),
                    PeriodEnd = table.Column<DateOnly>(type: "date", nullable: false),
                    DueOn = table.Column<DateOnly>(type: "date", nullable: false),
                    PlanCode = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    PlanName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Lines = table.Column<int>(type: "integer", nullable: false),
                    UnitPriceInCents = table.Column<long>(type: "bigint", nullable: false),
                    AddOnName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    AddOnQuantity = table.Column<int>(type: "integer", nullable: false),
                    AddOnConversations = table.Column<int>(type: "integer", nullable: false),
                    AddOnUnitPriceInCents = table.Column<long>(type: "bigint", nullable: false),
                    AmountInCents = table.Column<long>(type: "bigint", nullable: false),
                    Currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    PaidAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Settlement = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Note = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ClosedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AutomaticAttempts = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PaymentLinkId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PaymentLinkUrl = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    PaymentLinkExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BillingCharges", x => x.Id);
                    table.CheckConstraint("CK_BillingCharges_Amount", "\"AmountInCents\" >= 0");
                    table.CheckConstraint("CK_BillingCharges_Currency", "\"Currency\" = 'COP'");
                    table.CheckConstraint("CK_BillingCharges_Paid", "(\"Status\" = 'Paid') = (\"PaidAt\" IS NOT NULL)");
                    table.CheckConstraint("CK_BillingCharges_Period", "\"PeriodEnd\" > \"PeriodStart\"");
                    table.ForeignKey(
                        name: "FK_BillingCharges_BillingSubscriptions_SubscriptionId",
                        column: x => x.SubscriptionId,
                        principalTable: "BillingSubscriptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AiQuotaGrants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChargeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Conversations = table.Column<int>(type: "integer", nullable: false),
                    ValidFrom = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ValidUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiQuotaGrants", x => x.Id);
                    table.CheckConstraint("CK_AiQuotaGrants_Conversations", "\"Conversations\" > 0");
                    table.CheckConstraint("CK_AiQuotaGrants_Window", "\"ValidUntil\" > \"ValidFrom\"");
                    table.ForeignKey(
                        name: "FK_AiQuotaGrants_BillingCharges_ChargeId",
                        column: x => x.ChargeId,
                        principalTable: "BillingCharges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BillingPaymentAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChargeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Reference = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    GatewayTransactionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AmountInCents = table.Column<long>(type: "bigint", nullable: false),
                    Currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    MethodType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    StatusReason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    AttemptedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastCheckedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Checks = table.Column<int>(type: "integer", nullable: false),
                    ReviewReason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ReviewFlaggedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReviewedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BillingPaymentAttempts", x => x.Id);
                    table.CheckConstraint("CK_BillingPaymentAttempts_Amount", "\"AmountInCents\" >= 0");
                    table.ForeignKey(
                        name: "FK_BillingPaymentAttempts_BillingCharges_ChargeId",
                        column: x => x.ChargeId,
                        principalTable: "BillingCharges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiQuotaGrants_ChargeId",
                table: "AiQuotaGrants",
                column: "ChargeId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AiQuotaGrants_TenantId_ValidUntil",
                table: "AiQuotaGrants",
                columns: new[] { "TenantId", "ValidUntil" });

            migrationBuilder.CreateIndex(
                name: "IX_BillingAddOns_PlanId_Code",
                table: "BillingAddOns",
                columns: new[] { "PlanId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BillingCharges_OneOpenPerSubscription",
                table: "BillingCharges",
                column: "SubscriptionId",
                unique: true,
                filter: "\"Status\" = 'Open'");

            migrationBuilder.CreateIndex(
                name: "IX_BillingCharges_PaymentLinkId",
                table: "BillingCharges",
                column: "PaymentLinkId",
                unique: true,
                filter: "\"PaymentLinkId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_BillingCharges_SubscriptionId_PeriodStart",
                table: "BillingCharges",
                columns: new[] { "SubscriptionId", "PeriodStart" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BillingCharges_TenantId_PeriodStart",
                table: "BillingCharges",
                columns: new[] { "TenantId", "PeriodStart" });

            migrationBuilder.CreateIndex(
                name: "IX_BillingEvents_BodyHash",
                table: "BillingEvents",
                column: "BodyHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BillingEvents_NextAttemptAt",
                table: "BillingEvents",
                column: "NextAttemptAt",
                filter: "\"Status\" = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "IX_BillingEvents_ReceivedAt",
                table: "BillingEvents",
                column: "ReceivedAt");

            migrationBuilder.CreateIndex(
                name: "IX_BillingPaymentAttempts_GatewayTransactionId",
                table: "BillingPaymentAttempts",
                column: "GatewayTransactionId",
                unique: true,
                filter: "\"GatewayTransactionId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_BillingPaymentAttempts_OnePendingPerCharge",
                table: "BillingPaymentAttempts",
                column: "ChargeId",
                unique: true,
                filter: "\"Status\" = 'Pending' AND \"Kind\" <> 'PaymentLink'");

            migrationBuilder.CreateIndex(
                name: "IX_BillingPaymentAttempts_Reference",
                table: "BillingPaymentAttempts",
                column: "Reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BillingPaymentAttempts_ReviewFlaggedAt",
                table: "BillingPaymentAttempts",
                column: "ReviewFlaggedAt",
                filter: "\"ReviewReason\" IS NOT NULL AND \"ReviewedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_BillingPlans_Code",
                table: "BillingPlans",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BillingReviewItems_FlaggedAt",
                table: "BillingReviewItems",
                column: "FlaggedAt",
                filter: "\"ReviewedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_BillingReviewItems_TenantId_FlaggedAt",
                table: "BillingReviewItems",
                columns: new[] { "TenantId", "FlaggedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BillingSubscriptions_AddOnId",
                table: "BillingSubscriptions",
                column: "AddOnId");

            migrationBuilder.CreateIndex(
                name: "IX_BillingSubscriptions_NextRunAt",
                table: "BillingSubscriptions",
                column: "NextRunAt",
                filter: "\"NextRunAt\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_BillingSubscriptions_OneLivePerTenant",
                table: "BillingSubscriptions",
                column: "TenantId",
                unique: true,
                filter: "\"Status\" <> 'Cancelled'");

            migrationBuilder.CreateIndex(
                name: "IX_BillingSubscriptions_PlanId",
                table: "BillingSubscriptions",
                column: "PlanId");

            migrationBuilder.CreateIndex(
                name: "IX_BillingSubscriptions_TenantId_CreatedAt",
                table: "BillingSubscriptions",
                columns: new[] { "TenantId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiQuotaGrants");

            migrationBuilder.DropTable(
                name: "BillingEvents");

            migrationBuilder.DropTable(
                name: "BillingPaymentAttempts");

            migrationBuilder.DropTable(
                name: "BillingReviewItems");

            migrationBuilder.DropTable(
                name: "BillingCharges");

            migrationBuilder.DropTable(
                name: "BillingSubscriptions");

            migrationBuilder.DropTable(
                name: "BillingAddOns");

            migrationBuilder.DropTable(
                name: "BillingPlans");
        }
    }
}
