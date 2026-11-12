using System.Net;
using System.Net.Http.Json;
using Conversia.Application.DTOs.Contacts;
using Conversia.Application.Services;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Infrastructure.Persistence;
using Conversia.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>
/// The suppression of one contact on request of the customer (Ley 1581; docs/USERS_TENANTS_PERMISSIONS.md, "Contact suppression"): what
/// goes, what stays, and why the contact never comes back from its old messages while a new message opens a new contact.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class ContactSuppressionTests
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public ContactSuppressionTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private static string Url(Guid contactId) => $"/api/contacts/{contactId}/suppression";

    private async Task<Contact?> ContactByWaIdAsync(Guid tenantId, string waId)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Contacts.IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(c => c.TenantId == tenantId && c.WaId == waId);
    }

    private static long Unix(DateTime at) => new DateTimeOffset(at).ToUnixTimeSeconds();

    [Fact]
    public async Task Suppressing_DeletesTheContactItsConversationsMessagesOpportunitiesUsageAndProcessedDeliveries_AndNothingElse()
    {
        using var f = await StartChannelAsync(_factory, _client);
        using var other = await StartChannelAsync(_factory, _client);
        await ClearQueueAsync(_factory.Services);
        var waId = UniqueWaId();
        var keptWaId = UniqueWaId();
        await DeliverAsync(_client, f, Dialog360Samples.InboundText(id: $"wamid.S1-{Guid.NewGuid():N}", waId: waId));
        await DeliverAsync(_client, f, Dialog360Samples.InboundText(id: $"wamid.S2-{Guid.NewGuid():N}", waId: keptWaId));
        await DeliverAsync(_client, other, Dialog360Samples.InboundText(id: $"wamid.S3-{Guid.NewGuid():N}", waId: waId)); // same number, other company
        await DrainAsync(RunnerOf(_factory.Services));
        var contact = (await ContactByWaIdAsync(f.TenantId, waId))!;
        var second = await SeedConversationAsync(_factory.Services, f, new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc), [In(0), Out(10)], waId: null);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var conversation = await db.Conversations.IgnoreQueryFilters().SingleAsync(c => c.ContactId == contact.Id);
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "AiUsageConversations" ("TenantId", "Month", "ConversationId") VALUES ({f.TenantId}, DATE '2026-11-01', {conversation.Id}), ({f.TenantId}, DATE '2026-11-01', {second.ConversationId})
                """);
        }

        var otherBefore = await SnapshotAsync(_factory.Services, other.TenantId);
        var response = await f.Tenant.Owner.Client.PostAsync(Url(contact.Id), null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var counts = await ReadBodyAsync<ContactSuppressionResponse>(response);
        Assert.Equal((1, 1, 1, 1), (counts.Conversations, counts.Messages, counts.Opportunities, counts.WebhookEvents));

        var after = await SnapshotAsync(_factory.Services, f.TenantId);
        Assert.DoesNotContain(after.Contacts, c => c.WaId == waId);
        Assert.Contains(after.Contacts, c => c.WaId == keptWaId);
        Assert.DoesNotContain(after.Conversations, c => c.ContactId == contact.Id);
        Assert.Equal(2, after.Conversations.Count); // the other contact's and the seeded one
        Assert.Single(after.Events); // the other contact's delivery stays
        Assert.DoesNotContain(waId, after.Events.Single().RawPayload, StringComparison.Ordinal);
        Assert.Equal(otherBefore.Contacts.Count, (await SnapshotAsync(_factory.Services, other.TenantId)).Contacts.Count);
        Assert.NotNull(await ContactByWaIdAsync(other.TenantId, waId));

        using var check = _factory.Services.CreateScope();
        var usage = await check.ServiceProvider.GetRequiredService<ApplicationDbContext>().AiUsageConversations.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.TenantId == f.TenantId).Select(u => u.ConversationId).ToListAsync();
        Assert.Equal([second.ConversationId], usage);
    }

    [Fact]
    public async Task OldMessagesNeverBringTheContactBack_ButANewMessageOpensANewContact()
    {
        using var f = await StartChannelAsync(_factory, _client);
        await ClearQueueAsync(_factory.Services);
        var waId = UniqueWaId();
        var oldBody = Dialog360Samples.InboundText(id: $"wamid.OLD-{Guid.NewGuid():N}", waId: waId);
        await DeliverAsync(_client, f, oldBody);
        await DrainAsync(RunnerOf(_factory.Services));
        var contact = (await ContactByWaIdAsync(f.TenantId, waId))!;

        // A delivery of an old message still queued when the contact is suppressed.
        await DeliverAsync(_client, f, Dialog360Samples.InboundText(id: $"wamid.QUEUED-{Guid.NewGuid():N}", waId: waId, timestamp: Dialog360Samples.T0 + 120));
        Assert.Equal(HttpStatusCode.OK, (await f.Tenant.Owner.Client.PostAsync(Url(contact.Id), null)).StatusCode);
        // And the provider re-sending the first one (its stored delivery is gone, so it is accepted again).
        await DeliverAsync(_client, f, oldBody);
        await DrainAsync(RunnerOf(_factory.Services));

        Assert.Null(await ContactByWaIdAsync(f.TenantId, waId));
        Assert.Empty((await SnapshotAsync(_factory.Services, f.TenantId)).Messages);

        // The same person writes again after the suppression: a new contact, with nothing of the old one.
        await DeliverAsync(_client, f, Dialog360Samples.InboundText(id: $"wamid.NEW-{Guid.NewGuid():N}", waId: waId, timestamp: Unix(DateTime.UtcNow.AddMinutes(1))));
        await DrainAsync(RunnerOf(_factory.Services));

        var reborn = await ContactByWaIdAsync(f.TenantId, waId);
        Assert.NotNull(reborn);
        Assert.NotEqual(contact.Id, reborn!.Id);
        Assert.Single((await SnapshotAsync(_factory.Services, f.TenantId)).Messages);
    }

    [Fact]
    public async Task OnlyAPersonWithThePermission_CanSuppress_AndAContactOfAnotherCompanyIsNotFound()
    {
        using var f = await StartChannelAsync(_factory, _client);
        using var other = await StartChannelAsync(_factory, _client);
        var seeded = await SeedConversationAsync(_factory.Services, f, new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc), [In(0)]);
        var foreign = await SeedConversationAsync(_factory.Services, other, new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc), [In(0)]);
        using var viewer = await f.Tenant.AddMemberAsync("viewer", Permission.ConversationsView, Permission.ConversationsManage);
        using var allowed = await f.Tenant.AddMemberAsync("privacy", Permission.ContactsSuppress);

        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Client.PostAsync(Url(seeded.ContactId), null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await allowed.Client.PostAsync(Url(foreign.ContactId), null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await allowed.Client.PostAsync(Url(Guid.NewGuid()), null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await allowed.Client.PostAsync(Url(seeded.ContactId), null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await allowed.Client.PostAsync(Url(seeded.ContactId), null)).StatusCode); // already gone
        Assert.NotNull((await SnapshotAsync(_factory.Services, other.TenantId)).Contacts.SingleOrDefault(c => c.Id == foreign.ContactId));
    }

    [Fact]
    public async Task TheTrail_IsALogLineWithIdsAndCounts_NeverTheNumberOrTheName()
    {
        var capture = new LogCapture();
        using var host = WebhookHost(_factory, [], configureLogging: logging => logging.AddProvider(capture));
        using var f = await StartChannelAsync(_factory, _client);
        var waId = UniqueWaId();
        var seeded = await SeedConversationAsync(_factory.Services, f, new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc), [In(0, "Soy Laura, mi cédula es 1020304050")], waId: waId);
        using var hostOwner = host.CreateClient();
        hostOwner.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", f.Tenant.Owner.Token);

        Assert.Equal(HttpStatusCode.OK, (await hostOwner.PostAsync(Url(seeded.ContactId), null)).StatusCode);

        var line = Assert.Single(capture.Entries, e => e.Message.Contains("was suppressed", StringComparison.Ordinal));
        Assert.True(line.HasPropertyValue(seeded.ContactId.ToString()));
        Assert.False(line.Mentions(waId));
        Assert.False(line.Mentions("Laura"));
        Assert.False(line.Mentions("1020304050"));
        Assert.Equal(4201, ContactSuppressionService.ContactSuppressed.Id);
    }

    // ---- Second review round ------------------------------------------------------------------------

    private async Task<Guid> PlantEventAsync(ChannelFixture f, string payload, WebhookEventStatus status)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var delivery = WebhookEvent.Receive(f.TenantId, f.Channel.Id, System.Security.Cryptography.RandomNumberGenerator.GetBytes(32), payload, DateTime.UtcNow);
        if (status == WebhookEventStatus.Failed)
        {
            delivery.Fail("seeded failure");
        }
        else if (status == WebhookEventStatus.Processed)
        {
            delivery.MarkProcessed(DateTime.UtcNow);
        }

        db.Add(delivery);
        await db.SaveChangesAsync();
        return delivery.Id;
    }

    [Fact]
    public async Task FailedDeliveriesWithTheNumber_GoToo_AndAPendingOne_IsDeletedRightAfterItIsProcessed()
    {
        using var f = await StartChannelAsync(_factory, _client);
        await ClearQueueAsync(_factory.Services);
        var waId = UniqueWaId();
        var other = UniqueWaId();
        var seeded = await SeedConversationAsync(_factory.Services, f, new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc), [In(0)], waId: waId);
        var failed = await PlantEventAsync(f, Dialog360Samples.InboundText(id: $"wamid.F-{Guid.NewGuid():N}", waId: waId), WebhookEventStatus.Failed);
        var otherFailed = await PlantEventAsync(f, Dialog360Samples.InboundText(id: $"wamid.F2-{Guid.NewGuid():N}", waId: other), WebhookEventStatus.Failed);
        // Queued before the suppression, with this customer's old message and another customer's message.
        var pending = await PlantEventAsync(f, Dialog360Samples.TwoContacts(waId, other, $"wamid.P-{Guid.NewGuid():N}", $"wamid.P2-{Guid.NewGuid():N}"), WebhookEventStatus.Pending);

        Assert.Equal(HttpStatusCode.OK, (await f.Tenant.Owner.Client.PostAsync(Url(seeded.ContactId), null)).StatusCode);

        var after = await SnapshotAsync(_factory.Services, f.TenantId);
        Assert.DoesNotContain(after.Events, e => e.Id == failed);
        Assert.Contains(after.Events, e => e.Id == otherFailed);
        Assert.Contains(after.Events, e => e.Id == pending); // still queued: it may carry other customers' messages

        await DrainAsync(RunnerOf(_factory.Services));

        var processed = await SnapshotAsync(_factory.Services, f.TenantId);
        Assert.DoesNotContain(processed.Events, e => e.Id == pending); // deleted right after processing
        Assert.Null(await ContactByWaIdAsync(f.TenantId, waId));
        Assert.NotNull(await ContactByWaIdAsync(f.TenantId, other)); // the other customer's message is stored, only its raw copy went
        Assert.Single(processed.Messages);
    }

    [Fact]
    public async Task TheScanOfOldDeliveries_IsBatched_AndFindsTheNumberAmongThousandsOfRows()
    {
        using var f = await StartChannelAsync(_factory, _client);
        var waId = UniqueWaId();
        var seeded = await SeedConversationAsync(_factory.Services, f, new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc), [In(0)], waId: waId);
        const int rows = 3000;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            // Thousands of finished deliveries of other customers, and one in every 600 with the suppressed number.
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "WebhookEvents" ("Id", "TenantId", "ChannelId", "BodyHash", "RawPayload", "ReceivedAt", "Status", "Attempts", "NextAttemptAt", "CreatedAt", "IsSandbox")
                SELECT gen_random_uuid(), {f.TenantId}, {f.Channel.Id}, decode(md5(random()::text) || md5(random()::text), 'hex'),
                       jsonb_build_object('from', CASE WHEN i % 600 = 0 THEN {waId} ELSE '57399' || lpad(i::text, 7, '0') END, 'text', 'hola'),
                       now(), CASE WHEN i % 2 = 0 THEN 'Processed' ELSE 'Failed' END, 1, now(), now(), false
                FROM generate_series(1, {rows}) AS i
                """);
        }

        var response = await f.Tenant.Owner.Client.PostAsync(Url(seeded.ContactId), null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(rows / 600, (await ReadBodyAsync<ContactSuppressionResponse>(response)).WebhookEvents);
        Assert.Equal(rows - rows / 600, (await SnapshotAsync(_factory.Services, f.TenantId)).Events.Count);
        Assert.True(rows / ContactSuppressionStoreBatch > 1); // more than one batch was needed
    }

    private const int ContactSuppressionStoreBatch = Conversia.Infrastructure.Contacts.ContactSuppressionStore.ScanBatchSize;

    [Fact]
    public async Task TheMark_IsKeyed_AndAMarkMadeWithARetiredKeyStillDropsOldMessages()
    {
        var keyA = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
        var keyB = Convert.ToBase64String(Enumerable.Range(50, 32).Select(i => (byte)i).ToArray());
        using var before = WebhookHost(_factory, new() { ["Privacy:HashKey"] = keyA, ["Privacy:HashKeyId"] = "a" });
        using var after = WebhookHost(_factory, new() { ["Privacy:HashKey"] = keyB, ["Privacy:HashKeyId"] = "b", ["Privacy:RetiredHashKeys:a"] = keyA });
        using var f = await StartChannelAsync(_factory, _client);
        await ClearQueueAsync(_factory.Services);
        var waId = UniqueWaId();
        var seeded = await SeedConversationAsync(_factory.Services, f, new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc), [In(0)], waId: waId);
        using var owner = before.CreateClient();
        owner.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", f.Tenant.Owner.Token);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync(Url(seeded.ContactId), null)).StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var mark = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().ContactSuppressions.IgnoreQueryFilters().AsNoTracking().SingleAsync(m => m.TenantId == f.TenantId);
            Assert.Equal("a", mark.HashKeyId);
            var plain = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{f.TenantId:N}:{waId}")));
            Assert.NotEqual(plain, mark.WaIdHash);
            Assert.DoesNotContain(waId, mark.WaIdHash, StringComparison.Ordinal);
        }

        // After the rotation (key b current, key a retired) an old message of the number is still dropped by the new host's worker.
        await DeliverAsync(after.CreateClient(), f, Dialog360Samples.InboundText(id: $"wamid.ROT-{Guid.NewGuid():N}", waId: waId));
        await DrainAsync(RunnerOf(after.Services));
        Assert.Null(await ContactByWaIdAsync(f.TenantId, waId));
    }

    [Fact]
    public async Task ASuppressionAndADeliveryOfTheSameContactAtOnce_NeverLeaveTheContactOrItsOldMessages()
    {
        for (var round = 0; round < 5; round++)
        {
            using var f = await StartChannelAsync(_factory, _client);
            await ClearQueueAsync(_factory.Services);
            var waId = UniqueWaId();
            await DeliverAsync(_client, f, Dialog360Samples.InboundText(id: $"wamid.C0-{Guid.NewGuid():N}", waId: waId));
            await DrainAsync(RunnerOf(_factory.Services));
            var contact = (await ContactByWaIdAsync(f.TenantId, waId))!;
            await DeliverAsync(_client, f, Dialog360Samples.InboundText(id: $"wamid.C1-{Guid.NewGuid():N}", waId: waId, timestamp: Dialog360Samples.T0 + 300));

            await Task.WhenAll(DrainAsync(RunnerOf(_factory.Services)), f.Tenant.Owner.Client.PostAsync(Url(contact.Id), null));
            await DrainAsync(RunnerOf(_factory.Services));

            Assert.Null(await ContactByWaIdAsync(f.TenantId, waId));
            Assert.Empty((await SnapshotAsync(_factory.Services, f.TenantId)).Messages);
        }
    }

    [Fact]
    public async Task AMessageSentAtTheExactInstantOfTheSuppression_IsDropped_AndOneASecondLater_OpensANewContact()
    {
        using var f = await StartChannelAsync(_factory, _client);
        await ClearQueueAsync(_factory.Services);
        var waId = UniqueWaId();
        var seeded = await SeedConversationAsync(_factory.Services, f, At(Dialog360Samples.T0), [In(0)], waId: waId);
        var cutoff = Dialog360Samples.T0 + 60;
        using (var scope = _factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<Conversia.Application.Interfaces.IContactSuppressionStore>();
            Assert.NotNull(await store.SuppressAsync(f.TenantId, seeded.ContactId, f.Tenant.Owner.UserId, At(cutoff)));
        }

        await DeliverAsync(_client, f, Dialog360Samples.InboundText(id: $"wamid.EQ-{Guid.NewGuid():N}", waId: waId, timestamp: cutoff));
        await DrainAsync(RunnerOf(_factory.Services));
        Assert.Null(await ContactByWaIdAsync(f.TenantId, waId));

        await DeliverAsync(_client, f, Dialog360Samples.InboundText(id: $"wamid.GT-{Guid.NewGuid():N}", waId: waId, timestamp: cutoff + 1));
        await DrainAsync(RunnerOf(_factory.Services));
        Assert.NotNull(await ContactByWaIdAsync(f.TenantId, waId));
    }
}
