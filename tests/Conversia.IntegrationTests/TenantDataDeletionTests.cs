using System.Net;
using System.Net.Http.Json;
using Conversia.Domain.Common;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Infrastructure.Persistence;
using Conversia.Infrastructure.Tenants;
using Conversia.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>
/// The deletion of a cancelled company's data (Ley 1581; docs/PLATFORM_ADMIN.md, "Deleting a company's data"). The tables are enumerated
/// from the EF model, so a new table with a <c>TenantId</c> fails here until the deletion plan covers it and the test fills it.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class TenantDataDeletionTests
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public TenantDataDeletionTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private Conversia.Application.Privacy.PrivacyHasher Hasher => _factory.Services.GetRequiredService<Conversia.Application.Privacy.PrivacyHasher>();

    private TenantDataDeleter Deleter(int batchSize = 1000, TimeProvider? time = null)
        => new(_factory.Services.GetRequiredService<IServiceScopeFactory>(), new TenantDeletionOptions(false, 60, batchSize), time ?? TimeProvider.System,
            NullLogger<TenantDataDeleter>.Instance);

    /// <summary>Every table of the model with a <c>TenantId</c> column.</summary>
    private List<string> TenantTables()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return db.Model.GetEntityTypes().Where(t => t.FindProperty("TenantId") is not null).Select(t => t.GetTableName()!).Distinct().Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>Rows of the company in every table of <see cref="TenantTables"/> (plus its user permissions and its own row).</summary>
    private async Task<SortedDictionary<string, int>> CountRowsAsync(Guid tenantId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var table in TenantTables())
        {
#pragma warning disable EF1002 // table names come from the EF model
            counts[table] = await db.Database.SqlQueryRaw<int>($$"""SELECT COUNT(*)::int AS "Value" FROM "{{table}}" WHERE "TenantId" = {0}""", tenantId).SingleAsync();
#pragma warning restore EF1002
        }

        counts["UserPermissions"] = await db.Database.SqlQuery<int>(
            $"""SELECT COUNT(*)::int AS "Value" FROM "UserPermissions" up JOIN "Users" u ON u."Id" = up."UserId" WHERE u."TenantId" = {tenantId}""").SingleAsync();
        counts["Tenants"] = await db.Database.SqlQuery<int>($"""SELECT COUNT(*)::int AS "Value" FROM "Tenants" WHERE "Id" = {tenantId}""").SingleAsync();
        return counts;
    }

    /// <summary>A company with a row in every table its data lives in.</summary>
    private async Task<ChannelFixture> PopulatedCompanyAsync(TestAdmin admin)
    {
        var f = await StartChannelAsync(_factory, _client, phone: $"+57 302 {Random.Shared.Next(100, 999)} {Random.Shared.Next(1000, 9999)}");
        await ClearQueueAsync(_factory.Services);
        var waId = UniqueWaId();
        await DeliverAsync(_client, f, Dialog360Samples.InboundText(id: $"wamid.DEL-{Guid.NewGuid():N}", waId: waId));
        await DrainAsync(RunnerOf(_factory.Services));

        var seeded = await SeedConversationAsync(_factory.Services, f, new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc), [In(0, "Hola"), Out(30)]);
        await CreateAdvisorAsync(f.Tenant.Owner.Client, f.Branch.Id);
        using var member = await f.Tenant.AddMemberAsync("delmember", Permission.ConversationsView);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "AiUsageMonths" ("TenantId", "Month", "IsSandbox", "ConversationsAnalyzed", "Calls", "FailedCalls", "InputTokens", "OutputTokens",
                    "CacheReadTokens", "CacheWriteTokens", "EstimatedUsd", "WonDisagreements", "UpdatedAt")
                VALUES ({f.TenantId}, DATE '2026-10-01', false, 1, 1, 0, 100, 50, 0, 0, 0.001, 0, now())
                """);
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "AiUsageConversations" ("TenantId", "Month", "ConversationId") VALUES ({f.TenantId}, DATE '2026-10-01', {seeded.ConversationId})
                """);
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "MessageSendOutcomes" ("TenantId", "Day", "IsSandbox", "Outcome", "Count") VALUES ({f.TenantId}, DATE '2026-10-01', false, 'sent', 3)
                """);
            // A stored media object waiting to be deleted (docs/MESSAGING.md, "Media").
            db.MediaObjectDeletions.Add(MediaObjectDeletion.Create(f.TenantId, $"{f.TenantId:N}/2026/10/0123456789abcdef0123456789abcdef", DateTime.UtcNow));
            // A synced template of the line (docs/MESSAGING.md, "Templates"): the business's own text, deleted with the company.
            db.MessageTemplates.Add(MessageTemplate.Create(
                f.TenantId, f.Channel.Id, "recordatorio_cita", "es", TemplateCategory.Utility, TemplateStatus.Approved, "Hola {{1}}", true, DateTime.UtcNow));
            await db.SaveChangesAsync();

            // The LLM schedule's rows (docs/CLASSIFICATION.md): a window's counts and a batched request of the conversation.
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "AiAnalysisRuns" ("TenantId", "WindowStart", "WindowEnd", "Mode", "StartedAt", "LastActivityAt", "Claimed", "Analysed", "Unchanged",
                    "Skipped", "Discarded", "Failed", "Deferred", "Batched", "Calls")
                VALUES ({f.TenantId}, TIMESTAMP '2026-10-01 06:00', TIMESTAMP '2026-10-01 11:00', 'Nightly', now(), now(), 1, 1, 0, 0, 0, 0, 0, 1, 1)
                """);
            var batch = LlmBatch.Prepare(1, DateTime.UtcNow);
            db.LlmBatches.Add(batch);
            db.LlmBatchRequests.Add(LlmBatchRequest.For(
                batch.Id, f.TenantId, seeded.ConversationId, DateTime.UtcNow, new string('e', 64), false, null, OpportunityStage.New, null, DateTime.UtcNow));
            // The billing rows (docs/BILLING.md): a subscription with a paid charge, its attempt and its AI add-on grant.
            var plan = BillingPlan.Create("del" + Guid.NewGuid().ToString("N")[..12], "Plan", 100, 10);
            var addOn = BillingAddOn.Create(plan.Id, "ia", "IA", 10, 100);
            var subscription = BillingSubscription.Start(f.TenantId, plan.Id, new DateOnly(2026, 10, 1), 0, DateTime.UtcNow);
            subscription.StartPeriod(new DateOnly(2026, 10, 1));
            var charge = BillingCharge.Create(subscription, plan, 1, addOn, 1, new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 1), DateTime.UtcNow);
            var attempt = BillingPaymentAttempt.Begin(charge, PaymentAttemptKind.Automatic, PaymentMethodType.Card, DateTime.UtcNow);
            attempt.Resolve(PaymentAttemptStatus.Approved, null, DateTime.UtcNow);
            charge.MarkPaid(ChargeSettlement.Gateway, null, DateTime.UtcNow);
            db.AddRange(plan, addOn, subscription, charge, attempt, AiQuotaGrant.For(charge, DateTime.UtcNow, DateTime.UtcNow.AddDays(30)));
            await db.SaveChangesAsync();

            var code = $"ERR-19800301-{Random.Shared.Next(0, 999_999):D6}";
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "ErrorLogs" ("Id", "CreatedAt", "Code", "OccurredAt", "Source", "Kind", "Environment", "ServiceVersion", "Instance", "HttpMethod",
                    "Path", "Query", "Endpoint", "RouteValues", "StatusCode", "TraceId", "RequestId", "UserId", "TenantId", "UserAgent", "ExceptionType",
                    "ExceptionMessage", "ExceptionDetails", "Fingerprint", "Context")
                VALUES (gen_random_uuid(), now(), {code}, now(), 1, 1, 'Test', '0.0.0', 'seed', 'GET', '/seed', '', '', '', 500, '', '', NULL, {f.TenantId},
                    'seed-agent', 'System.Exception', 'seeded', 'seeded', 'aaaaaaaaaaaaaaaa', '')
                """);
        }

        // A suppressed contact leaves its mark (ContactSuppressions).
        var suppressed = await SeedConversationAsync(_factory.Services, f, new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc), [In(0, "Borren mis datos")]);
        Assert.Equal(HttpStatusCode.OK, (await f.Tenant.Owner.Client.PostAsync($"/api/contacts/{suppressed.ContactId}/suppression", null)).StatusCode);
        return f;
    }

    private static async Task CancelAndRequestDeletionAsync(TestAdmin admin, Guid tenantId)
    {
        Assert.Equal(HttpStatusCode.OK, (await admin.Client.PutAsJsonAsync($"{AdminTenantsUrl}/{tenantId}/status", new { status = "Cancelled", reason = "Fin del contrato" })).StatusCode);
        var name = (await GetAdminTenantAsync(admin, tenantId)).Name;
        Assert.Equal(HttpStatusCode.Accepted,
            (await admin.Client.PostAsJsonAsync($"{AdminTenantsUrl}/{tenantId}/data-deletion", new { confirmName = name, reason = "Solicitud del cliente" })).StatusCode);
    }

    [Fact]
    public void ThePlan_CoversEveryTableWithATenantId_ExceptTheTwoThatAreKeptOnPurpose()
    {
        var planned = TenantDataDeletionPlan.Steps.Select(s => s.Table).Where(t => t != "UserPermissions").Order(StringComparer.Ordinal).ToList();

        Assert.Equal(TenantTables().Except(TenantDataDeletionPlan.Kept).Order(StringComparer.Ordinal), planned);
        Assert.Equal(TenantDataDeletionPlan.Steps.Count, TenantDataDeletionPlan.Steps.Select(s => s.Table).Distinct().Count());
    }

    [Fact]
    public void ThePlan_CoversEveryTableThatPointsAtACompanyOrAtOneOfItsTables()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var planned = TenantDataDeletionPlan.Steps.Select(s => s.Table).ToHashSet(StringComparer.Ordinal);
        planned.Add("Tenants");

        // A table with a foreign key to a deleted table is deleted too (before it: the plan is children first).
        foreach (var entity in db.Model.GetEntityTypes())
        {
            foreach (var fk in entity.GetForeignKeys())
            {
                var principal = fk.PrincipalEntityType.GetTableName()!;
                if (planned.Contains(principal))
                {
                    Assert.Contains(entity.GetTableName()!, planned);
                    var order = TenantDataDeletionPlan.Steps.Select(s => s.Table).ToList();
                    if (principal != "Tenants" && principal != entity.GetTableName())
                    {
                        Assert.True(order.IndexOf(entity.GetTableName()!) < order.IndexOf(principal), $"{entity.GetTableName()} must be emptied before {principal}.");
                    }
                }
            }
        }
    }

    [Fact]
    public async Task DeletingACompany_LeavesNoRowWithItsTenantIdInAnyTable_KeepsTheTombstoneAndTheAudit_AndTouchesNoOtherCompany()
    {
        using var admin = await StartAdminAsync(_factory, _client);
        using var doomed = await PopulatedCompanyAsync(admin);
        using var other = await PopulatedCompanyAsync(admin);
        var name = (await GetAdminTenantAsync(admin, doomed.TenantId)).Name;

        var before = await CountRowsAsync(doomed.TenantId);
        foreach (var (table, rows) in before.Where(t => !TenantDataDeletionPlan.Kept.Contains(t.Key)))
        {
            Assert.True(rows > 0, $"The test company has no row in {table}: fill it, or the deletion of that table is not proved.");
        }

        var otherBefore = await CountRowsAsync(other.TenantId);

        await CancelAndRequestDeletionAsync(admin, doomed.TenantId);
        Assert.Equal(TenantDeletionRun.Completed, await Deleter(batchSize: 2).DeleteAsync(doomed.TenantId));

        var after = await CountRowsAsync(doomed.TenantId);
        foreach (var (table, rows) in after.Where(t => !TenantDataDeletionPlan.Kept.Contains(t.Key)))
        {
            Assert.True(rows == 0, $"{table} still holds {rows} row(s) of the deleted company.");
        }

        Assert.Equal(otherBefore, await CountRowsAsync(other.TenantId));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var tombstone = await db.TenantDeletions.AsNoTracking().SingleAsync(d => d.TenantId == doomed.TenantId);
        Assert.NotNull(tombstone.CompletedAt);
        Assert.Equal(Hasher.TenantName(name).Hex, tombstone.NameHash);
        var counts = tombstone.DeletedCounts();
        foreach (var (table, rows) in before.Where(t => !TenantDataDeletionPlan.Kept.Contains(t.Key)))
        {
            Assert.Equal(rows, counts.GetValueOrDefault(table));
        }

        var audit = await db.PlatformAuditLogs.AsNoTracking().Where(a => a.TenantId == doomed.TenantId).ToListAsync();
        var deleted = Assert.Single(audit, a => a.Action == PlatformAuditLog.Actions.TenantDataDeleted);
        Assert.Null(deleted.AdminId);
        Assert.Equal(ReasonCodes.DeletionCompleted, deleted.ReasonCode);
        Assert.DoesNotContain(name, deleted.Details, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(audit, a => a.Action == PlatformAuditLog.Actions.TenantStatusChanged); // the history stays

        // Gone for everybody: the administrator, the webhook, the old token.
        Assert.Equal(HttpStatusCode.NotFound, (await admin.Client.GetAsync($"{AdminTenantsUrl}/{doomed.TenantId}")).StatusCode);
        using var webhook = await PostWebhookAsync(_client, doomed.Path, doomed.Secret, Dialog360Samples.InboundText(id: $"wamid.GONE-{Guid.NewGuid():N}"));
        Assert.Equal(HttpStatusCode.Unauthorized, webhook.StatusCode);
        var gone = await doomed.Tenant.Owner.Client.GetAsync("/api/conversations");
        Assert.Equal(HttpStatusCode.Unauthorized, gone.StatusCode);
        var goneBody = await gone.Content.ReadAsStringAsync();
        Assert.Contains("tenant_gone", goneBody);
        Assert.Contains("ya no existe", goneBody);
        Assert.DoesNotContain("suspendida", goneBody);

        // Idempotent: there is nothing left to do.
        Assert.Equal(TenantDeletionRun.NotStarted, await Deleter().DeleteAsync(doomed.TenantId));
    }

    [Fact]
    public async Task ADeletionStoppedHalfWay_ResumesWithExactCounts_AndTheCompanyCannotBeReactivatedMeanwhile()
    {
        using var admin = await StartAdminAsync(_factory, _client);
        using var f = await StartChannelAsync(_factory, _client, phone: $"+57 303 {Random.Shared.Next(100, 999)} {Random.Shared.Next(1000, 9999)}");
        for (var i = 0; i < 3; i++)
        {
            await SeedConversationAsync(_factory.Services, f, new DateTime(2026, 9, 1 + i, 12, 0, 0, DateTimeKind.Utc), [In(0), Out(10), In(20)]);
        }

        await CancelAndRequestDeletionAsync(admin, f.TenantId);
        var deleter = Deleter(batchSize: 2);

        // The "crash": one batch of messages committed, then the process stopped.
        var step = TenantDataDeletionPlan.Steps.First(s => s.Table == "Messages");
        Assert.Equal(2, await deleter.DeleteBatchAsync(f.TenantId, step, CancellationToken.None));
        Assert.Equal(7, (await CountRowsAsync(f.TenantId))["Messages"]);

        var reactivate = await admin.Client.PutAsJsonAsync($"{AdminTenantsUrl}/{f.TenantId}/status", new { status = "Active" });
        Assert.Equal(HttpStatusCode.Conflict, reactivate.StatusCode);

        // The next run (the background job) finds it started and finishes it.
        Assert.True(await Deleter(batchSize: 2).RunAsync() >= 1);

        using var scope = _factory.Services.CreateScope();
        var tombstone = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().TenantDeletions.AsNoTracking().SingleAsync(d => d.TenantId == f.TenantId);
        Assert.NotNull(tombstone.CompletedAt);
        Assert.Equal(9, tombstone.DeletedCounts()["Messages"]);
        Assert.Equal(3, tombstone.DeletedCounts()["Conversations"]);
        Assert.Equal(1, tombstone.DeletedCounts()["Tenants"]);
    }

    [Fact]
    public async Task TheJob_StartsADeletionOnlyOnceTheRetentionWindowPassed_AuditedAsTheSystem()
    {
        using var admin = await StartAdminAsync(_factory, _client);
        using var tenant = await StartTenantAsync(_factory, _client, "scheduled");
        Assert.Equal(HttpStatusCode.OK, (await admin.Client.PutAsJsonAsync($"{AdminTenantsUrl}/{tenant.TenantId}/status", new { status = "Cancelled" })).StatusCode);

        await Deleter().RunAsync();
        Assert.Equal(1, (await CountRowsAsync(tenant.TenantId))["Tenants"]); // not due yet

        // The window passes (31 days later, as the clock of the job and of the lifecycle service see it).
        using (var scope = _factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.ExecuteSqlAsync($"""
                UPDATE "Tenants" SET "CancelledAt" = now() - interval '31 days', "DataDeletionDueAt" = now() - interval '1 day' WHERE "Id" = {tenant.TenantId}
                """);
        }

        await Deleter().RunAsync();

        var after = await CountRowsAsync(tenant.TenantId);
        Assert.Equal(0, after["Tenants"]);
        Assert.Equal(0, after["Users"]);
        using var check = _factory.Services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var started = await db.PlatformAuditLogs.AsNoTracking().SingleAsync(a => a.TenantId == tenant.TenantId && a.Action == PlatformAuditLog.Actions.TenantDataDeletionStarted);
        Assert.Null(started.AdminId);
        Assert.Equal(ReasonCodes.DeletionRetentionExpired, started.ReasonCode);
        var tombstone = await db.TenantDeletions.AsNoTracking().SingleAsync(d => d.TenantId == tenant.TenantId);
        Assert.Equal(TenantDeletionTrigger.Scheduled, tombstone.Trigger);
        Assert.Null(tombstone.RequestedBy);
    }

    // ---- Second review round ------------------------------------------------------------------------

    private async Task<DateTime> DueAtAsync(Guid tenantId)
    {
        using var scope = _factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Tenants.AsNoTracking().SingleAsync(t => t.Id == tenantId)).DataDeletionDueAt!.Value;
    }

    [Fact]
    public async Task TheJob_StartsADeletionAtTheExactInstantTheWindowEnds_AndNotAMicrosecondBefore()
    {
        using var admin = await StartAdminAsync(_factory, _client);
        using var tenant = await StartTenantAsync(_factory, _client, "boundary");
        Assert.Equal(HttpStatusCode.OK, (await admin.Client.PutAsJsonAsync($"{AdminTenantsUrl}/{tenant.TenantId}/status", new { status = "Cancelled" })).StatusCode);
        var due = await DueAtAsync(tenant.TenantId);

        await Deleter(time: new FixedTimeProvider(due.AddTicks(-10))).RunAsync();
        Assert.Equal(1, (await CountRowsAsync(tenant.TenantId))["Tenants"]);

        await Deleter(time: new FixedTimeProvider(due)).RunAsync();
        Assert.Equal(0, (await CountRowsAsync(tenant.TenantId))["Tenants"]);
    }

    [Fact]
    public async Task TwoDeletersAtOnce_OneCompletes_TheOtherSeesItDone_AndTheCountsAreExact()
    {
        using var admin = await StartAdminAsync(_factory, _client);
        using var f = await StartChannelAsync(_factory, _client, phone: $"+57 304 {Random.Shared.Next(100, 999)} {Random.Shared.Next(1000, 9999)}");
        for (var i = 0; i < 4; i++)
        {
            await SeedConversationAsync(_factory.Services, f, new DateTime(2026, 8, 1 + i, 12, 0, 0, DateTimeKind.Utc), [In(0), Out(10), In(20), Out(30), In(40)]);
        }

        await CancelAndRequestDeletionAsync(admin, f.TenantId);

        var results = await Task.WhenAll(Deleter(batchSize: 3).DeleteAsync(f.TenantId), Deleter(batchSize: 3).DeleteAsync(f.TenantId));

        Assert.Single(results, r => r == TenantDeletionRun.Completed);
        Assert.Single(results, r => r is TenantDeletionRun.CompletedElsewhere or TenantDeletionRun.NotStarted);
        using var scope = _factory.Services.CreateScope();
        var tombstone = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().TenantDeletions.AsNoTracking().SingleAsync(d => d.TenantId == f.TenantId);
        Assert.Equal(20, tombstone.DeletedCounts()["Messages"]);
        Assert.Equal(4, tombstone.DeletedCounts()["Conversations"]);
        Assert.Equal(1, tombstone.DeletedCounts()["Tenants"]);
    }

    [Fact]
    public async Task ACrashWhileFinishing_LeavesTheCompanyRowAndTheOpenTombstone_AndTheNextRunFinishesIt()
    {
        using var admin = await StartAdminAsync(_factory, _client);
        using var tenant = await StartTenantAsync(_factory, _client, "crashfinish");
        await CancelAndRequestDeletionAsync(admin, tenant.TenantId);
        var crashing = Deleter();
        crashing.BeforeFinishCommit = _ => throw new InvalidOperationException("simulated crash");

        await Assert.ThrowsAsync<InvalidOperationException>(() => crashing.DeleteAsync(tenant.TenantId));

        Assert.Equal(1, (await CountRowsAsync(tenant.TenantId))["Tenants"]); // the finishing transaction rolled back
        using (var scope = _factory.Services.CreateScope())
        {
            Assert.Null((await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().TenantDeletions.AsNoTracking().SingleAsync(d => d.TenantId == tenant.TenantId)).CompletedAt);
        }

        Assert.Equal(TenantDeletionRun.Completed, await Deleter().DeleteAsync(tenant.TenantId));
        Assert.Equal(0, (await CountRowsAsync(tenant.TenantId))["Tenants"]);
    }

    [Fact]
    public async Task RowsThatKeepAppearing_StopTheRunAfterItsPasses_MarkItStuck_AndRaiseTheMonitoringFlag()
    {
        using var admin = await StartAdminAsync(_factory, _client);
        using var f = await StartChannelAsync(_factory, _client, phone: $"+57 305 {Random.Shared.Next(100, 999)} {Random.Shared.Next(1000, 9999)}");
        await CancelAndRequestDeletionAsync(admin, f.TenantId);
        var deleter = Deleter();
        // As a defect would: a row of the company appears right before every finishing check, so no pass ever finds the tables empty.
        deleter.BeforeFinishCheck = async _ =>
        {
            using var scope = _factory.Services.CreateScope();
            var code = $"ERR-19800302-{Random.Shared.Next(0, 999_999):D6}";
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.ExecuteSqlAsync($"""
                INSERT INTO "ErrorLogs" ("Id", "CreatedAt", "Code", "OccurredAt", "Source", "Kind", "Environment", "ServiceVersion", "Instance", "HttpMethod",
                    "Path", "Query", "Endpoint", "RouteValues", "StatusCode", "TraceId", "RequestId", "UserId", "TenantId", "UserAgent", "ExceptionType",
                    "ExceptionMessage", "ExceptionDetails", "Fingerprint", "Context")
                VALUES (gen_random_uuid(), now(), {code}, now(), 1, 1, 'Test', '0.0.0', 'seed', 'GET', '/seed', '', '', '', 500, '', '', NULL, {f.TenantId},
                    'seed-agent', 'System.Exception', 'seeded', 'seeded', 'aaaaaaaaaaaaaaaa', '')
                """);
        };

        var result = await deleter.DeleteAsync(f.TenantId);

        Assert.Equal(TenantDeletionRun.Stuck, result);
        using (var scope = _factory.Services.CreateScope())
        {
            Assert.NotNull((await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().TenantDeletions.AsNoTracking().SingleAsync(d => d.TenantId == f.TenantId)).StuckSince);
        }

        var overview = await admin.Client.GetAsync("/api/admin/monitoring");
        var body = await overview.Content.ReadAsStringAsync();
        Assert.Contains("tenant_deletion_stuck", body);
        Assert.DoesNotContain(f.TenantId.ToString(), body); // a count, no company named

        Assert.Equal(TenantDeletionRun.Completed, await Deleter().DeleteAsync(f.TenantId)); // once the writer stopped, the next run finishes
    }
}
