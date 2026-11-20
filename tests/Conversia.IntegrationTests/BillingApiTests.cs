using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Conversia.API.Configuration;
using Conversia.Application.Billing;
using Conversia.Application.Classification.Llm;
using Conversia.Application.DTOs.Billing;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Infrastructure.Billing;
using Conversia.Infrastructure.Classification.Usage;
using Conversia.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>
/// The recurring charge end to end (docs/BILLING.md): the real API, the real Wompi adapter and a real PostgreSQL, with Wompi's HTTP answered by
/// <see cref="FakeWompi"/> (no call ever leaves the process). Keys are test-only values with the sandbox prefixes.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class BillingApiTests
{
    internal const string EventsSecret = "test_events_integration000";

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _anonymous;

    public BillingApiTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _anonymous = factory.CreateClient();
    }

    private sealed record BillingHost(WebApplicationFactory<Program> Host, FakeWompi Wompi);

    private (WebApplicationFactory<Program> Host, FakeWompi Wompi) Host()
    {
        var created = _factory.GetOrAddHost("billing-wompi", () =>
        {
            var wompi = new FakeWompi();
            var host = _factory.WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Billing:Wompi:Environment", "Sandbox");
                builder.UseSetting("Billing:Wompi:PublicKey", "pub_test_integration000");
                builder.UseSetting("Billing:Wompi:PrivateKey", "prv_test_integration000");
                builder.UseSetting("Billing:Wompi:EventsSecret", EventsSecret);
                builder.UseSetting("Billing:Wompi:IntegritySecret", "test_integrity_integration000");
                builder.UseSetting("Billing:Events:RateLimitPerMinute", "100000");
                builder.ConfigureServices(services =>
                    services.AddHttpClient<IPaymentGateway, WompiGateway>().ConfigurePrimaryHttpMessageHandler(() => wompi));
            });
            return new BillingHost(host, wompi);
        });
        return (created.Host, created.Wompi);
    }

    private static HttpClient Client(WebApplicationFactory<Program> host, string token)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<(TestTenant Tenant, HttpClient Owner, HttpClient Admin, FakeWompi Wompi, WebApplicationFactory<Program> Host, Guid PlanId)> SubscribedAsync(long pricePesos = 90_000, int lines = 2)
    {
        var (host, wompi) = Host();
        var tenant = await StartTenantAsync(_factory, _anonymous, "billing");
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.Tenants.SingleAsync(t => t.Id == tenant.TenantId)).SetMaxActiveLines(lines);
            await db.SaveChangesAsync();
        }

        using var adminSession = await StartAdminAsync(_factory, _anonymous, "billing-admin");
        var admin = Client(host, adminSession.Token);
        var plan = await admin.PostAsJsonAsync("/api/admin/billing/plans", new { code = "p" + Guid.NewGuid().ToString("N")[..10], name = "Profesional", pricePerLinePesos = pricePesos, includedAiConversations = 300 });
        Assert.Equal(HttpStatusCode.Created, plan.StatusCode);
        var planId = (await ReadBodyAsync<PlanResponse>(plan)).Id;
        var started = await admin.PostAsJsonAsync($"/api/admin/tenants/{tenant.TenantId}/billing/start", new { planId, trialDays = 0 });
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        await RunJobAsync(host);
        return (tenant, Client(host, tenant.Owner.Token), admin, wompi, host, planId);
    }

    private static async Task RunJobAsync(WebApplicationFactory<Program> host)
        => await host.Services.GetRequiredService<BillingJobRunner>().RunOnceAsync();

    private static async Task AddCardAsync(HttpClient owner, string token = "tok_test_4242")
    {
        var setup = await ReadBodyAsync<PaymentMethodSetupResponse>(await owner.GetAsync("/api/billing/payment-method/setup"));
        Assert.Equal("pub_test_integration000", setup.PublicKey);
        Assert.Equal("https://sandbox.wompi.co/v1", setup.ApiBaseUrl);
        var response = await owner.PutAsJsonAsync("/api/billing/payment-method", new
        {
            type = "Card", token, acceptanceToken = setup.AcceptanceToken, personalDataToken = setup.PersonalDataToken, acceptedTerms = true,
            cardBrand = "VISA", cardLastFour = "4242",
        });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task The_owner_adds_a_card_pays_the_open_charge_and_sees_it_paid_with_the_card_masked()
    {
        var (tenant, owner, _, wompi, _, _) = await SubscribedAsync(pricePesos: 90_000, lines: 2);
        var account = await ReadBodyAsync<BillingAccountResponse>(await owner.GetAsync("/api/billing"));
        var charge = account.OpenCharge!;
        Assert.Equal(2 * 90_000 * 100L, charge.AmountInCents);

        var token = "tok_test_" + Guid.NewGuid().ToString("N");
        await AddCardAsync(owner, token);
        var paid = await owner.PostAsync($"/api/billing/charges/{charge.Id}/pay", null);

        Assert.Equal(PaymentAttemptStatus.Approved, (await ReadBodyAsync<PayNowResponse>(paid)).Status);
        account = await ReadBodyAsync<BillingAccountResponse>(await owner.GetAsync("/api/billing"));
        Assert.Null(account.OpenCharge);
        Assert.Equal(BillingChargeStatus.Paid, account.Charges[0].Status);
        Assert.Equal("VISA •••• 4242", account.PaymentMethodLabel);
        var sent = wompi.Transactions.Values.Single(t => t.Reference == wompi.LastReference);
        Assert.Equal(charge.AmountInCents, sent.Amount);
        Assert.Equal(WompiSignatures.Integrity(sent.Reference, sent.Amount, "COP", "test_integrity_integration000"), sent.Signature);
        Assert.True(wompi.SourceRequests.Single(r => r.Token == token).AcceptedBoth);

        using var scope = _factory.Services.CreateScope();
        var audits = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().PlatformAuditLogs.AsNoTracking()
            .Where(a => a.TenantId == tenant.TenantId).Select(a => a.Action).ToListAsync();
        Assert.Contains(PlatformAuditLog.Actions.BillingChargePaid, audits);
        Assert.Contains(PlatformAuditLog.Actions.BillingPaymentMethodChanged, audits);
        // Nothing of the card but the label and the gateway's id.
        var stored = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().BillingSubscriptions.IgnoreQueryFilters().AsNoTracking().SingleAsync(s => s.TenantId == tenant.TenantId);
        Assert.Matches("^[0-9]+$", stored.PaymentSourceId);
        tenant.Dispose();
    }

    [Fact]
    public async Task A_pending_nequi_payment_is_settled_by_a_signed_event_after_re_reading_the_transaction()
    {
        var (tenant, owner, _, wompi, host, _) = await SubscribedAsync();
        await AddCardAsync(owner);
        var charge = (await ReadBodyAsync<BillingAccountResponse>(await owner.GetAsync("/api/billing"))).OpenCharge!;
        wompi.NextStatus = "PENDING";
        Assert.Equal(PaymentAttemptStatus.Pending, (await ReadBodyAsync<PayNowResponse>(await owner.PostAsync($"/api/billing/charges/{charge.Id}/pay", null))).Status);
        wompi.NextStatus = "APPROVED";

        // A second payment cannot start while this one waits.
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsync($"/api/billing/charges/{charge.Id}/pay", null)).StatusCode);

        var transaction = wompi.Transactions.Values.Single(t => t.Reference == wompi.LastReference);
        transaction.Status = "APPROVED"; // the payer approved in the app
        var anonymous = host.CreateClient();

        // The event says DECLINED and a tiny amount: neither is trusted, the transaction is re-read.
        var body = FakeWompi.Event(transaction.Id, "DECLINED", 1, EventsSecret);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.PostAsync("/api/billing/wompi/events", Json(body))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.PostAsync("/api/billing/wompi/events", Json(body))).StatusCode); // re-sent
        var reserialized = FakeWompi.Event(transaction.Id, "DECLINED", 1, EventsSecret, timestamp: JsonDocument.Parse(body).RootElement.GetProperty("timestamp").GetInt64(), indented: true);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.PostAsync("/api/billing/wompi/events", Json(reserialized))).StatusCode); // same event, other bytes
        await RunJobAsync(host);

        var account = await ReadBodyAsync<BillingAccountResponse>(await owner.GetAsync("/api/billing"));
        Assert.Equal(BillingChargeStatus.Paid, account.Charges.Single(c => c.Id == charge.Id).Status);
        using var scope = _factory.Services.CreateScope();
        var events = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().BillingEvents.AsNoTracking().Where(e => e.TransactionId == transaction.Id).ToListAsync();
        Assert.Equal(BillingEventStatus.Processed, Assert.Single(events).Status);
        tenant.Dispose();
    }

    [Fact]
    public async Task A_pending_payment_of_a_charge_marked_paid_by_hand_is_still_polled_and_its_approval_goes_to_review()
    {
        var (tenant, owner, admin, wompi, host, _) = await SubscribedAsync();
        await AddCardAsync(owner, "tok_test_" + Guid.NewGuid().ToString("N"));
        var charge = (await ReadBodyAsync<BillingAccountResponse>(await owner.GetAsync("/api/billing"))).OpenCharge!;
        wompi.NextStatus = "PENDING";
        await owner.PostAsync($"/api/billing/charges/{charge.Id}/pay", null);
        wompi.NextStatus = "APPROVED";
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync($"/api/admin/tenants/{tenant.TenantId}/billing/charges/{charge.Id}/mark-paid", new { reason = "Transferencia" })).StatusCode);

        wompi.Transactions.Values.Single(t => t.Reference == wompi.LastReference).Status = "APPROVED"; // the card payment went through too
        using (var scope = _factory.Services.CreateScope())
        {
            // The poll is due (as two minutes later).
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.ExecuteSqlAsync($"""
                UPDATE "BillingPaymentAttempts" SET "AttemptedAt" = "AttemptedAt" - interval '10 minutes', "LastCheckedAt" = NULL WHERE "TenantId" = {tenant.TenantId};
                UPDATE "BillingSubscriptions" SET "NextRunAt" = now() - interval '1 second' WHERE "TenantId" = {tenant.TenantId};
                """);
        }

        await RunJobAsync(host);

        var account = await ReadBodyAsync<AdminBillingAccountResponse>(await admin.GetAsync($"/api/admin/tenants/{tenant.TenantId}/billing"));
        var attempt = Assert.Single(account.Charges.Single(c => c.Id == charge.Id).Attempts);
        Assert.Equal(PaymentAttemptStatus.Approved, attempt.Status);
        Assert.Equal("duplicate_payment", attempt.ReviewReason);
        var overview = await admin.GetFromJsonAsync<JsonElement>("/api/admin/monitoring");
        Assert.True(overview.GetProperty("backlog").GetProperty("billingNeedsReview").GetInt32() >= 1);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync($"/api/admin/tenants/{tenant.TenantId}/billing/attempts/{attempt.Id}/review", new { markFailed = false, reason = "Reembolsado en Wompi" })).StatusCode);
        account = await ReadBodyAsync<AdminBillingAccountResponse>(await admin.GetAsync($"/api/admin/tenants/{tenant.TenantId}/billing"));
        Assert.Null(account.Charges.Single(c => c.Id == charge.Id).Attempts.Single().ReviewReason);
        tenant.Dispose();
    }

    [Fact]
    public async Task A_company_reactivated_after_its_subscription_was_cancelled_can_be_billed_again_keeping_the_history()
    {
        var (tenant, owner, admin, _, _, planId) = await SubscribedAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync($"/api/admin/tenants/{tenant.TenantId}/billing/cancel", new { atPeriodEnd = false, reasonCode = "admin.contract_ended" })).StatusCode);

        var restarted = await admin.PostAsJsonAsync($"/api/admin/tenants/{tenant.TenantId}/billing/start", new { planId, trialDays = 0 });

        Assert.Equal(HttpStatusCode.OK, restarted.StatusCode);
        var account = await ReadBodyAsync<AdminBillingAccountResponse>(restarted);
        Assert.Equal(SubscriptionStatus.Active, account.Status);
        Assert.NotNull(account.OpenCharge);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/admin/tenants/{tenant.TenantId}/billing/start", new { planId, trialDays = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync("/api/billing/cancel", null)).StatusCode); // acts on the live one
        using var scope = _factory.Services.CreateScope();
        Assert.Equal(2, await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().BillingSubscriptions.IgnoreQueryFilters().CountAsync(s => s.TenantId == tenant.TenantId));
        tenant.Dispose();
    }

    [Fact]
    public async Task The_job_the_owner_and_the_administrator_racing_on_one_charge_never_charge_it_twice()
    {
        var (tenant, owner, admin, wompi, host, _) = await SubscribedAsync();
        await AddCardAsync(owner, "tok_test_" + Guid.NewGuid().ToString("N"));
        var charge = (await ReadBodyAsync<BillingAccountResponse>(await owner.GetAsync("/api/billing"))).OpenCharge!;
        using (var scope = _factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.ExecuteSqlAsync($"""
                UPDATE "BillingCharges" SET "NextAttemptAt" = now() - interval '1 minute' WHERE "Id" = {charge.Id};
                UPDATE "BillingSubscriptions" SET "NextRunAt" = now() - interval '1 second' WHERE "TenantId" = {tenant.TenantId};
                """);
        }

        wompi.NextStatus = "PENDING";
        var racers = new List<Task>();
        for (var i = 0; i < 4; i++)
        {
            racers.Add(RunJobAsync(host));
            racers.Add(owner.PostAsync($"/api/billing/charges/{charge.Id}/pay", null));
        }

        racers.Add(admin.PostAsJsonAsync($"/api/admin/tenants/{tenant.TenantId}/billing/charges/{charge.Id}/mark-paid", new { reason = "Transferencia" }));
        await Task.WhenAll(racers);

        using var check = _factory.Services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var attempts = await db.BillingPaymentAttempts.IgnoreQueryFilters().AsNoTracking().Where(a => a.ChargeId == charge.Id).ToListAsync();
        Assert.True(attempts.Count(a => a.Status == PaymentAttemptStatus.Pending) <= 1, "more than one pending attempt");
        Assert.True(attempts.Count <= 1, $"{attempts.Count} attempts for one charge");
        Assert.Equal(1, await db.BillingCharges.IgnoreQueryFilters().CountAsync(c => c.TenantId == tenant.TenantId));
        Assert.Equal(BillingChargeStatus.Paid, (await db.BillingCharges.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == charge.Id)).Status);
        tenant.Dispose();
    }

    [Fact]
    public async Task An_approved_payment_that_matches_nothing_of_ours_becomes_a_durable_review_item_the_administrator_closes()
    {
        var (host, wompi) = Host();
        using var adminSession = await StartAdminAsync(_factory, _anonymous, "billing-review");
        var admin = Client(host, adminSession.Token);
        var id = "tx-foreign-" + Guid.NewGuid().ToString("N");
        wompi.Transactions[id] = new FakeWompi.Transaction { Id = id, Reference = "someone-elses", Amount = 777_00, Signature = "-", Status = "APPROVED" };

        Assert.Equal(HttpStatusCode.OK, (await host.CreateClient().PostAsync("/api/billing/wompi/events", Json(FakeWompi.Event(id, "APPROVED", 777_00, EventsSecret)))).StatusCode);
        await RunJobAsync(host);
        await host.Services.GetRequiredService<BillingJobRunner>().PurgeOldEventsAsync(); // the events' retention never takes the review with it

        var items = await admin.GetFromJsonAsync<List<BillingReviewItemResponse>>("/api/admin/billing/review-items");
        var item = Assert.Single(items!, i => i.TransactionId == id);
        Assert.Equal(("unmatched_payment", 777_00L), (item.Reason, item.AmountInCents));
        var overview = await admin.GetFromJsonAsync<JsonElement>("/api/admin/monitoring");
        Assert.True(overview.GetProperty("backlog").GetProperty("billingNeedsReview").GetInt32() >= 1);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync($"/api/admin/billing/review-items/{item.Id}/review", new { reason = "Pago de otra integración" })).StatusCode);
        items = await admin.GetFromJsonAsync<List<BillingReviewItemResponse>>("/api/admin/billing/review-items");
        Assert.DoesNotContain(items!, i => i.Id == item.Id);
    }

    [Fact]
    public async Task A_company_left_suspended_for_billing_after_its_subscription_was_cancelled_is_lifted_by_the_job()
    {
        var (tenant, owner, admin, _, host, _) = await SubscribedAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync($"/api/admin/tenants/{tenant.TenantId}/billing/cancel", new { atPeriodEnd = false, reasonCode = "admin.contract_ended" })).StatusCode);
        using (var scope = host.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<Conversia.Application.Interfaces.ITenantBillingLifecycle>()
                .SuspendForBillingAsync(tenant.TenantId, Conversia.Domain.Common.ReasonCodes.BillingPastDue); // left behind (a failed resume)
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/api/branches")).StatusCode);
        await RunJobAsync(host);

        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/branches")).StatusCode);
        tenant.Dispose();
    }

    [Fact]
    public async Task Non_payment_cancels_the_company_the_subscription_and_the_charge_on_the_real_database()
    {
        var (tenant, owner, _, _, host, _) = await SubscribedAsync();
        using (var scope = host.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<Conversia.Application.Interfaces.ITenantBillingLifecycle>()
                .SuspendForBillingAsync(tenant.TenantId, Conversia.Domain.Common.ReasonCodes.BillingPastDue);
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.ExecuteSqlAsync($"""
                UPDATE "BillingSubscriptions" SET "BillingSuspendedAt" = now() - interval '40 days', "NextRunAt" = now() - interval '1 second' WHERE "TenantId" = {tenant.TenantId};
                UPDATE "BillingCharges" SET "DueOn" = "DueOn" - 50 WHERE "TenantId" = {tenant.TenantId};
                """);
        }

        await RunJobAsync(host);

        using var check = _factory.Services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(TenantStatus.Cancelled, (await db.Tenants.AsNoTracking().SingleAsync(t => t.Id == tenant.TenantId)).Status);
        Assert.Equal(SubscriptionStatus.Cancelled, (await db.BillingSubscriptions.IgnoreQueryFilters().AsNoTracking().SingleAsync(s => s.TenantId == tenant.TenantId)).Status);
        Assert.Equal(BillingChargeStatus.Failed, (await db.BillingCharges.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.TenantId == tenant.TenantId)).Status);
        _ = owner;
        tenant.Dispose();
    }

    [Fact]
    public async Task A_company_cancelled_by_the_administrator_stops_being_billed_on_the_next_run_and_can_be_billed_again_once_reactivated()
    {
        var (tenant, owner, admin, wompi, host, planId) = await SubscribedAsync();
        await AddCardAsync(owner, "tok_test_" + Guid.NewGuid().ToString("N"));
        var charge = (await ReadBodyAsync<BillingAccountResponse>(await owner.GetAsync("/api/billing"))).OpenCharge!;
        var cancelled = await admin.PutAsJsonAsync($"/api/admin/tenants/{tenant.TenantId}/status", new { status = "Cancelled", reasonCode = "admin.contract_ended", reason = "Fin del contrato" });
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        using (var scope = _factory.Services.CreateScope())
        {
            // The attempt would be due now.
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.ExecuteSqlAsync($"""
                UPDATE "BillingCharges" SET "NextAttemptAt" = now() - interval '1 minute' WHERE "Id" = {charge.Id};
                UPDATE "BillingSubscriptions" SET "NextRunAt" = now() - interval '1 second' WHERE "TenantId" = {tenant.TenantId};
                """);
        }

        var sentBefore = wompi.Transactions.Count;
        await RunJobAsync(host);
        await RunJobAsync(host);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.Equal(SubscriptionStatus.Cancelled, (await db.BillingSubscriptions.IgnoreQueryFilters().AsNoTracking().SingleAsync(s => s.TenantId == tenant.TenantId)).Status);
            Assert.Equal(BillingChargeStatus.Void, (await db.BillingCharges.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == charge.Id)).Status);
            Assert.False(await db.BillingPaymentAttempts.IgnoreQueryFilters().AnyAsync(a => a.TenantId == tenant.TenantId));
        }

        Assert.Equal(sentBefore, wompi.Transactions.Count);

        // Reactivated before the deletion: billing starts again with a new subscription (the cancelled one stays as history).
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/admin/tenants/{tenant.TenantId}/status", new { status = "Active", reasonCode = "admin.contract_signed", reason = "Renovó" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/admin/tenants/{tenant.TenantId}/billing/start", new { planId, trialDays = 0 })).StatusCode);
        tenant.Dispose();
    }

    [Fact]
    public async Task A_forged_event_is_refused_and_stores_nothing()
    {
        var (host, _) = Host();
        var anonymous = host.CreateClient();
        var forged = FakeWompi.Event("tx-forged-" + Guid.NewGuid().ToString("N"), "APPROVED", 100, "test_events_wrong");

        var response = await anonymous.PostAsync("/api/billing/wompi/events", Json(forged));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var scope = _factory.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().BillingEvents.AnyAsync(e => forged.Contains(e.TransactionId!)));
    }

    [Fact]
    public async Task A_stale_or_future_signed_event_is_a_replay_and_stores_nothing()
    {
        var (host, _) = Host();
        var anonymous = host.CreateClient();
        var id = "tx-old-" + Guid.NewGuid().ToString("N");
        var old = FakeWompi.Event(id, "APPROVED", 100, EventsSecret, timestamp: DateTimeOffset.UtcNow.AddHours(-73).ToUnixTimeSeconds());
        var future = FakeWompi.Event(id, "APPROVED", 100, EventsSecret, timestamp: DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds());
        var withinRetries = FakeWompi.Event(id, "PENDING", 100, EventsSecret, timestamp: DateTimeOffset.UtcNow.AddHours(-25).ToUnixTimeSeconds());

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/billing/wompi/events", Json(old))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/billing/wompi/events", Json(future))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.PostAsync("/api/billing/wompi/events", Json(withinRetries))).StatusCode);

        using var scope = _factory.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().BillingEvents.CountAsync(e => e.TransactionId == id));
    }

    [Fact]
    public async Task Anonymous_junk_from_one_address_never_starves_the_signed_events()
    {
        var wompi = new FakeWompi();
        var host = _factory.GetOrAddHost("billing-wompi-limited", () => _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Billing:Wompi:Environment", "Sandbox");
            builder.UseSetting("Billing:Wompi:PublicKey", "pub_test_integration000");
            builder.UseSetting("Billing:Wompi:PrivateKey", "prv_test_integration000");
            builder.UseSetting("Billing:Wompi:EventsSecret", EventsSecret);
            builder.UseSetting("Billing:Wompi:IntegritySecret", "test_integrity_integration000");
            builder.UseSetting("Billing:Events:FailedRateLimitPerMinute", "2");
            builder.ConfigureServices(services => services.AddHttpClient<IPaymentGateway, WompiGateway>().ConfigurePrimaryHttpMessageHandler(() => wompi));
        }));
        var anonymous = host.CreateClient();
        var junk = FakeWompi.Event("tx-junk", "APPROVED", 100, "test_events_wrong");

        var answers = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            answers.Add((await anonymous.PostAsync("/api/billing/wompi/events", Json(junk))).StatusCode);
        }

        Assert.Equal([HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests], answers);
        var signed = FakeWompi.Event("tx-signed-" + Guid.NewGuid().ToString("N"), "APPROVED", 100, EventsSecret);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.PostAsync("/api/billing/wompi/events", Json(signed))).StatusCode);
    }

    [Fact]
    public async Task Without_gateway_keys_the_events_endpoint_does_not_exist_and_payments_answer_503()
    {
        using var tenant = await StartTenantAsync(_factory, _anonymous, "billing-nokeys");
        Assert.Equal(HttpStatusCode.NotFound, (await _anonymous.PostAsync("/api/billing/wompi/events", Json("{}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await tenant.Owner.Client.GetAsync("/api/billing")).StatusCode); // no subscription yet
    }

    [Fact]
    public async Task Billing_is_for_the_owner_or_a_holder_of_the_permission_and_never_crosses_companies()
    {
        var (tenant, owner, _, _, host, _) = await SubscribedAsync();
        var charge = (await ReadBodyAsync<BillingAccountResponse>(await owner.GetAsync("/api/billing"))).OpenCharge!;
        using var member = await tenant.AddMemberAsync("billing-member", Permission.OrganizationView);
        using var other = await StartTenantAsync(_factory, _anonymous, "billing-other");

        Assert.Equal(HttpStatusCode.Forbidden, (await Client(host, member.Token).GetAsync("/api/billing")).StatusCode);
        // Another company's owner, with a subscription of its own, cannot touch this company's charge.
        var (_, otherOwner, _, _, _, _) = await SubscribedAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await otherOwner.PostAsync($"/api/billing/charges/{charge.Id}/payment-link", null)).StatusCode);

        using var billing = await tenant.AddMemberAsync("billing-holder", Permission.BillingManage);
        Assert.Equal(HttpStatusCode.OK, (await Client(host, billing.Token).GetAsync("/api/billing")).StatusCode);
        tenant.Dispose();
    }

    [Fact]
    public async Task A_company_suspended_for_billing_can_still_reach_its_billing_page_and_pay()
    {
        var (tenant, owner, admin, _, host, _) = await SubscribedAsync();
        using (var scope = host.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<Conversia.Application.Interfaces.ITenantBillingLifecycle>()
                .SuspendForBillingAsync(tenant.TenantId, Conversia.Domain.Common.ReasonCodes.BillingPastDue);
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/api/branches")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/billing")).StatusCode);
        var link = await owner.PostAsync($"/api/billing/charges/{(await ReadBodyAsync<BillingAccountResponse>(await owner.GetAsync("/api/billing"))).OpenCharge!.Id}/payment-link", null);
        Assert.StartsWith("https://checkout.wompi.co/l/", (await ReadBodyAsync<PaymentLinkResponse>(link)).Url);
        _ = admin;
        tenant.Dispose();
    }

    [Fact]
    public async Task The_administrator_marks_a_charge_paid_by_transfer_and_the_billing_suspension_is_lifted()
    {
        var (tenant, owner, admin, _, host, _) = await SubscribedAsync();
        var charge = (await ReadBodyAsync<BillingAccountResponse>(await owner.GetAsync("/api/billing"))).OpenCharge!;
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.Database.ExecuteSqlAsync($"""UPDATE "BillingSubscriptions" SET "BillingSuspendedAt" = now() WHERE "TenantId" = {tenant.TenantId}""");
            await scope.ServiceProvider.GetRequiredService<Conversia.Application.Interfaces.ITenantBillingLifecycle>()
                .SuspendForBillingAsync(tenant.TenantId, Conversia.Domain.Common.ReasonCodes.BillingPastDue);
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/admin/tenants/{tenant.TenantId}/billing/charges/{charge.Id}/mark-paid", new { reason = " " })).StatusCode);
        var marked = await admin.PostAsJsonAsync($"/api/admin/tenants/{tenant.TenantId}/billing/charges/{charge.Id}/mark-paid", new { reason = "Transferencia Bancolombia" });

        Assert.Equal(HttpStatusCode.NoContent, marked.StatusCode);
        var account = await ReadBodyAsync<AdminBillingAccountResponse>(await admin.GetAsync($"/api/admin/tenants/{tenant.TenantId}/billing"));
        Assert.Equal(ChargeSettlement.Manual, account.Charges.Single(c => c.Id == charge.Id).Settlement);
        Assert.Null(account.BillingSuspendedAt);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/branches")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/admin/tenants/{tenant.TenantId}/billing/charges/{charge.Id}/void", new { reason = "x" })).StatusCode);
        tenant.Dispose();
    }

    [Fact]
    public async Task Plans_have_unique_codes_a_subscription_is_one_per_company_and_cancelling_now_voids_what_is_owed()
    {
        var (tenant, owner, admin, _, _, planId) = await SubscribedAsync();
        var plans = await ReadBodyAsync<List<PlanResponse>>(await admin.GetAsync("/api/admin/billing/plans"));
        var code = plans.Single(p => p.Id == planId).Code;

        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/admin/billing/plans", new { code, name = "Otro", pricePerLinePesos = 1, includedAiConversations = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/admin/tenants/{tenant.TenantId}/billing/start", new { planId, trialDays = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/api/admin/billing/plans")).StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized ? HttpStatusCode.Forbidden : HttpStatusCode.OK);

        var addOn = await admin.PostAsJsonAsync($"/api/admin/billing/plans/{planId}/add-ons", new { code = "ia-500", name = "500 conversaciones", conversations = 500, pricePesos = 40_000 });
        Assert.Equal(HttpStatusCode.Created, addOn.StatusCode);
        var addOnId = (await ReadBodyAsync<AddOnResponse>(addOn)).Id;
        var changed = await admin.PutAsJsonAsync($"/api/admin/tenants/{tenant.TenantId}/billing", new { planId, billedLinesOverride = 5, addOnId, addOnQuantity = 2 });
        var account = await ReadBodyAsync<AdminBillingAccountResponse>(changed);
        Assert.Equal(5 * 90_000 * 100L + 2 * 40_000 * 100L, account.NextChargeAmountInCents); // from the next period: no proration

        var cancel = await admin.PostAsJsonAsync($"/api/admin/tenants/{tenant.TenantId}/billing/cancel", new { atPeriodEnd = false, reasonCode = "admin.customer_request" });
        Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);
        account = await ReadBodyAsync<AdminBillingAccountResponse>(await admin.GetAsync($"/api/admin/tenants/{tenant.TenantId}/billing"));
        Assert.Equal(SubscriptionStatus.Cancelled, account.Status);
        Assert.All(account.Charges, c => Assert.Equal(BillingChargeStatus.Void, c.Status));
        tenant.Dispose();
    }

    [Fact]
    public async Task The_owner_cancels_at_period_end_and_undoes_it()
    {
        var (tenant, owner, _, _, _, _) = await SubscribedAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync("/api/billing/cancel", null)).StatusCode);
        Assert.True((await ReadBodyAsync<BillingAccountResponse>(await owner.GetAsync("/api/billing"))).CancelAtPeriodEnd);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync("/api/billing/cancel/undo", null)).StatusCode);
        Assert.False((await ReadBodyAsync<BillingAccountResponse>(await owner.GetAsync("/api/billing"))).CancelAtPeriodEnd);
        tenant.Dispose();
    }

    [Fact]
    public async Task A_paid_add_on_and_the_plan_raise_the_ai_quota_the_meter_admits()
    {
        var (tenant, owner, admin, _, host, planId) = await SubscribedAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.Tenants.SingleAsync(t => t.Id == tenant.TenantId)).SetAiEnabled(true, "terms-test", DateTime.UtcNow);
            await db.SaveChangesAsync();
        }

        var meter = new AiUsageMeter(_factory.Services.GetRequiredService<IServiceScopeFactory>(), AiQuotaSettings.Default with { }, TimeProvider.System, NullLogger<AiUsageMeter>.Instance);
        for (var i = 0; i < 300; i++)
        {
            await meter.RecordSuccessAsync(new AiUsageRecord(tenant.TenantId, Guid.NewGuid(), false, new LlmUsage(1, 1, 0, 0), 0m));
        }

        Assert.Equal(AiAdmission.TenantExhausted, await meter.CheckAdmissionAsync(tenant.TenantId, isSandbox: false)); // the plan's 300

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var charge = await db.BillingCharges.IgnoreQueryFilters().FirstAsync(c => c.TenantId == tenant.TenantId);
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "AiQuotaGrants" ("Id", "TenantId", "ChargeId", "Conversations", "ValidFrom", "ValidUntil", "CreatedAt")
                VALUES ({Guid.NewGuid()}, {tenant.TenantId}, {charge.Id}, 50, now() - interval '1 day', now() + interval '1 day', now())
                """);
        }

        Assert.Equal(AiAdmission.Allowed, await meter.CheckAdmissionAsync(tenant.TenantId, isSandbox: false));
        _ = (owner, admin, host, planId);
        tenant.Dispose();
    }

    [Theory]
    [InlineData("Sandbox", "pub_prod_x", "prv_test_x", "test_events_x", "test_integrity_x", "Billing:Wompi:PublicKey")]
    [InlineData("Production", "pub_prod_x", "prv_prod_x", "test_events_x", "prod_integrity_x", "Billing:Wompi:EventsSecret")]
    [InlineData("Sandbox", "pub_test_x", "", "test_events_x", "test_integrity_x", "Billing:Wompi:PrivateKey")]
    [InlineData("Staging", "pub_test_x", "prv_test_x", "test_events_x", "test_integrity_x", "Billing:Wompi:Environment")]
    public void Keys_of_the_wrong_environment_stop_the_api_naming_the_key_never_the_value(string environment, string pub, string prv, string events, string integrity, string key)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Billing:Wompi:Environment"] = environment,
            ["Billing:Wompi:PublicKey"] = pub,
            ["Billing:Wompi:PrivateKey"] = prv,
            ["Billing:Wompi:EventsSecret"] = events,
            ["Billing:Wompi:IntegritySecret"] = integrity,
        }).Build();

        var failure = Assert.Throws<InvalidOperationException>(() => BillingServiceCollectionExtensions.GetValidatedWompiOptions(configuration));

        Assert.Contains(key, failure.Message);
        Assert.DoesNotContain(pub + "\"", failure.Message);
        Assert.DoesNotContain("integrity_x", failure.Message);
    }

    [Fact]
    public void Retry_days_from_configuration_are_validated()
    {
        var bad = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Billing:Dunning:RetryDays"] = "3,1" }).Build();
        var good = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Billing:Dunning:RetryDays"] = "2, 4", ["Billing:Dunning:CancelAfterSuspendedDays"] = "0" }).Build();

        Assert.Contains("Billing:Dunning:RetryDays", Assert.Throws<InvalidOperationException>(() => BillingServiceCollectionExtensions.GetValidatedSettings(bad)).Message);
        var settings = BillingServiceCollectionExtensions.GetValidatedSettings(good);
        Assert.Equal([2, 4], settings.RetryDays);
        Assert.Equal(0, settings.CancelAfterSuspendedDays);
        Assert.Null(BillingServiceCollectionExtensions.GetValidatedWompiOptions(new ConfigurationBuilder().Build()));
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");
}

/// <summary>Answers the Wompi calls the adapter makes, in memory, the way the documentation describes them. Never a real network call.</summary>
internal sealed class FakeWompi : HttpMessageHandler
{
    internal sealed class Transaction
    {
        public required string Id { get; init; }

        public required string Reference { get; init; }

        public required long Amount { get; init; }

        public required string Signature { get; init; }

        public string Status { get; set; } = "APPROVED";
    }

    internal sealed record SourceRequest(string Token, bool AcceptedBoth);

    private int _next;

    public ConcurrentDictionary<string, Transaction> Transactions { get; } = new();

    public ConcurrentBag<SourceRequest> SourceRequests { get; } = [];

    public string NextStatus { get; set; } = "APPROVED";

    public string? LastReference { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
        var isPrivate = request.Headers.Authorization?.Parameter?.StartsWith("prv_test_", StringComparison.Ordinal) == true;

        if (path == "/v1/merchants/info" && request.Headers.TryGetValues("x-merchant-public-key", out _))
        {
            return Answer(HttpStatusCode.OK, new
            {
                data = new
                {
                    presigned_acceptance = new { acceptance_token = "eyJ.policy.sig", permalink = "https://wompi.example/terminos.pdf", type = "END_USER_POLICY" },
                    presigned_personal_data_auth = new { acceptance_token = "eyJ.personal.sig", permalink = "https://wompi.example/datos.pdf", type = "PERSONAL_DATA_AUTH" },
                },
            });
        }

        if (!isPrivate)
        {
            return Answer(HttpStatusCode.Unauthorized, new { error = new { type = "INVALID_ACCESS_TOKEN" } });
        }

        if (path == "/v1/payment_sources" && request.Method == HttpMethod.Post)
        {
            SourceRequests.Add(new SourceRequest(body!["token"]!.GetValue<string>(),
                body["acceptance_token"]?.GetValue<string>() == "eyJ.policy.sig" && body["accept_personal_auth"]?.GetValue<string>() == "eyJ.personal.sig"));
            return Answer(HttpStatusCode.Created, new { data = new { id = 4000 + Interlocked.Increment(ref _next), public_data = new { type = "CARD" }, type = "CARD", status = "AVAILABLE" } });
        }

        if (path == "/v1/transactions" && request.Method == HttpMethod.Post)
        {
            var transaction = new Transaction
            {
                Id = $"tx-{Guid.NewGuid():N}",
                Reference = body!["reference"]!.GetValue<string>(),
                Amount = body["amount_in_cents"]!.GetValue<long>(),
                Signature = body["signature"]!.GetValue<string>(),
                Status = NextStatus,
            };
            Transactions[transaction.Id] = transaction;
            LastReference = transaction.Reference;
            return Answer(HttpStatusCode.Created, Data(transaction));
        }

        if (path.StartsWith("/v1/transactions/", StringComparison.Ordinal) && request.Method == HttpMethod.Get)
        {
            return Transactions.TryGetValue(path["/v1/transactions/".Length..], out var found)
                ? Answer(HttpStatusCode.OK, Data(found))
                : Answer(HttpStatusCode.NotFound, new { error = new { type = "NOT_FOUND_ERROR" } });
        }

        if (path == "/v1/payment_links" && request.Method == HttpMethod.Post)
        {
            return Answer(HttpStatusCode.Created, new { data = new { id = $"lnk{Interlocked.Increment(ref _next)}" } });
        }

        return Answer(HttpStatusCode.NotFound, new { error = new { type = "NOT_FOUND_ERROR" } });
    }

    private static object Data(Transaction t) => new
    {
        data = new { id = t.Id, status = t.Status, amount_in_cents = t.Amount, currency = "COP", reference = t.Reference, status_message = (string?)null },
    };

    private static HttpResponseMessage Answer(HttpStatusCode status, object body)
        => new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    /// <summary>A <c>transaction.updated</c> event signed with <paramref name="secret"/> the documented way.</summary>
    public static string Event(string transactionId, string status, long amount, string secret, long? timestamp = null, bool indented = false)
    {
        var at = timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var checksum = WompiSignatures.EventChecksum([transactionId, status, amount.ToString()], at.ToString(), secret);
        return JsonSerializer.Serialize(new
        {
            @event = "transaction.updated",
            data = new { transaction = new { id = transactionId, status, amount_in_cents = amount, currency = "COP", reference = "whatever" } },
            environment = "test",
            signature = new { properties = new[] { "transaction.id", "transaction.status", "transaction.amount_in_cents" }, checksum },
            timestamp = at,
            sent_at = "2026-11-12T10:00:00.000Z",
        }, new JsonSerializerOptions { WriteIndented = indented });
    }
}
