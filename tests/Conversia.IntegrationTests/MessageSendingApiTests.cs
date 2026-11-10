using System.Net;
using System.Net.Http.Json;
using Conversia.Application.DTOs.Conversations;
using Conversia.Application.Messaging;
using Conversia.Domain.Enums;
using Conversia.Infrastructure.Persistence;
using Conversia.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>
/// <c>POST /api/conversations/{id}/messages</c> against a stand-in provider: who may send, what is stored once the provider accepts,
/// what each refusal looks like, and that the provider's echo of a sent message never duplicates it.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class MessageSendingApiTests
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly MessagingHost _host;

    public MessageSendingApiTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _host = MessagingHostOf(factory);
    }

    /// <summary>A channel with credentials and one conversation in which the customer wrote a minute ago.</summary>
    private async Task<(ChannelFixture Fixture, Seeded Conversation, HttpClient Owner, string WaId)> StartConversationAsync(bool credentials = true, bool closed = false)
    {
        var f = await StartChannelAsync(_factory, _client);
        var owner = _host.ClientOf(f.Tenant.Owner);
        if (credentials)
        {
            await SetCredentialsAsync(owner, f.Channel.Id);
        }

        var waId = UniqueWaId();
        var seeded = await SeedConversationAsync(_factory.Services, f, DateTime.UtcNow.AddMinutes(-1), [In(0)], waId: waId, closed: closed);
        return (f, seeded, owner, waId);
    }

    // ---- Sent --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ASentMessage_GoesToTheCustomer_AndIsStoredAsOutbound_TheWayIngestionWouldStoreIt()
    {
        var (f, conversation, owner, waId) = await StartConversationAsync();
        using var _ = f;
        _host.Sender.Answer(f.Channel.Id, () => WhatsAppSendResult.Sent("wamid.SENT-OK"));

        var response = await SendMessageAsync(owner, conversation.ConversationId, "  Hola, con gusto te ayudo.  ");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var sent = await ReadBodyAsync<MessageResponse>(response);
        Assert.Equal(nameof(MessageDirection.Outbound), sent.Direction);
        Assert.Equal("text", sent.Type);
        Assert.Equal("Hola, con gusto te ayudo.", sent.Text);

        var call = Assert.Single(_host.Sender.SentBy(f.Channel.Id));
        Assert.Equal(waId, call.RecipientWaId);
        Assert.Equal("Hola, con gusto te ayudo.", call.Text);

        var snapshot = await SnapshotAsync(_factory.Services, f.TenantId);
        var stored = Assert.Single(snapshot.Messages, m => m.Direction == MessageDirection.Outbound);
        Assert.Equal(sent.Id, stored.Id);
        Assert.Equal("wamid.SENT-OK", stored.ProviderMessageId);
        Assert.Equal(conversation.ConversationId, stored.ConversationId);
        Assert.Equal(f.Channel.Id, stored.ChannelId);
        Assert.Equal(f.Tenant.Owner.UserId, stored.CreatedBy);

        var updated = Assert.Single(snapshot.Conversations);
        Assert.Equal(stored.SentAt, updated.FirstResponseAt);
        Assert.Equal(stored.SentAt, updated.LastMessageAt);
        Assert.True(updated.ClassificationDirty);
        Assert.NotNull(updated.ClassificationRequestedAt);

        var detail = await owner.GetFromJsonAsync<ConversationDetailResponse>($"/api/conversations/{conversation.ConversationId}");
        Assert.Equal(2, detail!.MessageCount);
        Assert.Equal("Hola, con gusto te ayudo.", detail.Messages[^1].Text);
        Assert.False(detail.Unanswered);
    }

    [Fact]
    public async Task TheEchoOfASentMessage_ArrivingThroughTheWebhook_IsNotStoredAgain()
    {
        using var f = await StartChannelAsync(_factory, _client);
        using var owner = _host.ClientOf(f.Tenant.Owner);
        await SetCredentialsAsync(owner, f.Channel.Id);
        await ClearQueueAsync(_factory.Services);
        var runner = RunnerOf(_factory.Services);

        // The customer writes through the real webhook, so the conversation is the one ingestion made.
        var waId = UniqueWaId();
        await DeliverAsync(_client, f, Dialog360Samples.InboundText($"wamid.IN-{Guid.NewGuid():N}", waId: waId));
        await DrainAsync(runner);
        var conversationId = Assert.Single((await SnapshotAsync(_factory.Services, f.TenantId)).Conversations).Id;

        var providerId = $"wamid.ECHOED-{Guid.NewGuid():N}";
        _host.Sender.Answer(f.Channel.Id, () => WhatsAppSendResult.Sent(providerId));
        Assert.Equal(HttpStatusCode.Created, (await SendMessageAsync(owner, conversationId, "Hola Laura")).StatusCode);

        // 360dialog echoes the message it just sent, under the same id.
        await DeliverAsync(_client, f, Dialog360Samples.OutboundEcho(providerId, to: waId, text: "Hola Laura"));
        await DrainAsync(runner);

        var snapshot = await SnapshotAsync(_factory.Services, f.TenantId);
        Assert.Equal(2, snapshot.Messages.Count);
        Assert.Single(snapshot.Messages, m => m.ProviderMessageId == providerId);
        Assert.Single(snapshot.Conversations);
        var echo = snapshot.Events[^1];
        Assert.Equal(WebhookEventStatus.Processed, echo.Status);
        Assert.Equal(0, echo.StoredMessageCount);
        Assert.Equal(1, echo.DuplicateMessageCount);
    }

    [Fact]
    public async Task AnEchoIngestedBeforeTheSendIsRecorded_IsTheMessage_NotASecondOne()
    {
        using var f = await StartChannelAsync(_factory, _client);
        using var owner = _host.ClientOf(f.Tenant.Owner);
        await SetCredentialsAsync(owner, f.Channel.Id);
        await ClearQueueAsync(_factory.Services);
        var runner = RunnerOf(_factory.Services);
        var waId = UniqueWaId();
        await DeliverAsync(_client, f, Dialog360Samples.InboundText($"wamid.IN-{Guid.NewGuid():N}", waId: waId));
        await DrainAsync(runner);
        var conversationId = Assert.Single((await SnapshotAsync(_factory.Services, f.TenantId)).Conversations).Id;

        // The provider's echo is processed while the send is still waiting for the provider's answer.
        var providerId = $"wamid.RACE-{Guid.NewGuid():N}";
        _host.Sender.Answer(f.Channel.Id, () =>
        {
            DeliverAsync(_client, f, Dialog360Samples.OutboundEcho(providerId, to: waId, text: "Hola Laura")).GetAwaiter().GetResult();
            DrainAsync(runner).GetAwaiter().GetResult();
            return WhatsAppSendResult.Sent(providerId);
        });

        var response = await SendMessageAsync(owner, conversationId, "Hola Laura");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var snapshot = await SnapshotAsync(_factory.Services, f.TenantId);
        var stored = Assert.Single(snapshot.Messages, m => m.ProviderMessageId == providerId);
        Assert.Equal(stored.Id, (await ReadBodyAsync<MessageResponse>(response)).Id);
        Assert.Equal(2, snapshot.Messages.Count);
    }

    // ---- Who may send ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task OnlyThoseWhoManageConversations_MaySend()
    {
        var (f, conversation, owner, _) = await StartConversationAsync();
        using var __ = f;
        using var viewerActor = await f.Tenant.AddMemberAsync("viewer", Permission.ConversationsView, Permission.OrganizationManage);
        using var managerActor = await f.Tenant.AddMemberAsync("manager", Permission.ConversationsManage);
        using var viewer = _host.ClientOf(viewerActor);
        using var manager = _host.ClientOf(managerActor);
        using var anonymous = _host.ClientWith(null);

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendMessageAsync(anonymous, conversation.ConversationId)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendMessageAsync(viewer, conversation.ConversationId)).StatusCode);
        Assert.Empty(_host.Sender.SentBy(f.Channel.Id));

        Assert.Equal(HttpStatusCode.Created, (await SendMessageAsync(manager, conversation.ConversationId)).StatusCode);
        Assert.Single(_host.Sender.SentBy(f.Channel.Id));
    }

    [Fact]
    public async Task AnotherTenantsConversation_IsNotFound_AndNothingIsSentOrStored()
    {
        var (a, conversation, _, _) = await StartConversationAsync();
        var (b, _, ownerB, _) = await StartConversationAsync();
        using var _a = a;
        using var _b = b;

        var foreign = await SendMessageAsync(ownerB, conversation.ConversationId);
        var unknown = await SendMessageAsync(ownerB, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(await unknown.Content.ReadAsStringAsync(), await foreign.Content.ReadAsStringAsync());
        Assert.Empty(_host.Sender.SentBy(a.Channel.Id));
        Assert.Empty(_host.Sender.SentBy(b.Channel.Id));
        Assert.DoesNotContain((await SnapshotAsync(_factory.Services, a.TenantId)).Messages, m => m.Direction == MessageDirection.Outbound);
    }

    // ---- Refused before the provider ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ABlankText_IsA400UnderText(string? text)
    {
        var (f, conversation, owner, _) = await StartConversationAsync();
        using var _ = f;

        await AssertValidationErrorUnderAsync(await SendMessageAsync(owner, conversation.ConversationId, text), "Text");
        await AssertValidationErrorUnderAsync(await SendMessageAsync(owner, conversation.ConversationId, new string('x', 4097)), "Text");
        Assert.Empty(_host.Sender.SentBy(f.Channel.Id));
    }

    [Fact]
    public async Task AChannelWithoutCredentials_AnInactiveChannel_AndAClosedConversation_AreDistinct409s()
    {
        var (noKey, noKeyConversation, noKeyOwner, _) = await StartConversationAsync(credentials: false);
        var (off, offConversation, offOwner, _) = await StartConversationAsync();
        var (closed, closedConversation, closedOwner, _) = await StartConversationAsync(closed: true);
        using var _1 = noKey;
        using var _2 = off;
        using var _3 = closed;
        Assert.Equal(HttpStatusCode.OK, (await offOwner.PostAsync($"{ChannelsUrl}/{off.Channel.Id}/deactivate", null)).StatusCode);

        var cases = new[]
        {
            (Response: await SendMessageAsync(noKeyOwner, noKeyConversation.ConversationId), Code: "provider_credentials_missing", Channel: noKey.Channel.Id),
            (Response: await SendMessageAsync(offOwner, offConversation.ConversationId), Code: "channel_inactive", Channel: off.Channel.Id),
            (Response: await SendMessageAsync(closedOwner, closedConversation.ConversationId), Code: "conversation_closed", Channel: closed.Channel.Id),
        };

        foreach (var (response, expected, channelId) in cases)
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var (code, body) = await ReadProblemCodeAsync(response);
            Assert.Equal(expected, code);
            AssertNoInternalsLeaked(body);
            Assert.Empty(_host.Sender.SentBy(channelId));
        }
    }

    // ---- The provider said no ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(WhatsAppSendStatus.InvalidCredentials, HttpStatusCode.Conflict, "provider_credentials_invalid")]
    [InlineData(WhatsAppSendStatus.RecipientNotAllowed, HttpStatusCode.UnprocessableEntity, "recipient_not_allowed")]
    [InlineData(WhatsAppSendStatus.LimitReached, HttpStatusCode.TooManyRequests, "provider_limit_reached")]
    [InlineData(WhatsAppSendStatus.Rejected, HttpStatusCode.UnprocessableEntity, "provider_rejected")]
    [InlineData(WhatsAppSendStatus.TransientFailure, HttpStatusCode.ServiceUnavailable, "provider_unavailable")]
    public async Task EachProviderRefusal_HasItsOwnAnswer_LeaksNothing_AndStoresNothing(WhatsAppSendStatus status, HttpStatusCode expectedStatus, string expectedCode)
    {
        var (f, conversation, owner, waId) = await StartConversationAsync();
        using var _ = f;
        _host.Sender.Answer(f.Channel.Id, () => WhatsAppSendResult.Failed(status));

        var response = await SendMessageAsync(owner, conversation.ConversationId);

        Assert.Equal(expectedStatus, response.StatusCode);
        var (code, body) = await ReadProblemCodeAsync(response);
        Assert.Equal(expectedCode, code);
        AssertNoInternalsLeaked(body);
        Assert.DoesNotContain(waId, body);
        Assert.DoesNotContain("d360", body, StringComparison.OrdinalIgnoreCase);

        var snapshot = await SnapshotAsync(_factory.Services, f.TenantId);
        Assert.DoesNotContain(snapshot.Messages, m => m.Direction == MessageDirection.Outbound);
        Assert.Null(Assert.Single(snapshot.Conversations).FirstResponseAt);
    }

    // ---- The budget --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task SendingIsLimitedPerUser_ThenAnswers429WithRetryAfter()
    {
        var limited = MessagingHostOf(_factory, "messaging-rate-limit", new() { ["Messaging:SendRateLimitPerMinute"] = "2" });
        using var f = await StartChannelAsync(_factory, _client);
        using var managerActor = await f.Tenant.AddMemberAsync("sender", Permission.ConversationsManage);
        using var owner = limited.ClientOf(f.Tenant.Owner);
        using var manager = limited.ClientOf(managerActor);
        await SetCredentialsAsync(owner, f.Channel.Id);
        var conversation = await SeedConversationAsync(_factory.Services, f, DateTime.UtcNow.AddMinutes(-1), [In(0)]);

        Assert.Equal(HttpStatusCode.Created, (await SendMessageAsync(owner, conversation.ConversationId)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await SendMessageAsync(owner, conversation.ConversationId)).StatusCode);
        var third = await SendMessageAsync(owner, conversation.ConversationId);

        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.NotNull(third.Headers.RetryAfter);
        Assert.Equal(2, limited.Sender.SentBy(f.Channel.Id).Count);

        // Another user has a budget of their own.
        Assert.Equal(HttpStatusCode.Created, (await SendMessageAsync(manager, conversation.ConversationId)).StatusCode);
    }

    [Fact]
    public async Task SendingIsAlsoLimitedPerCompany_WhoeverSends_AndOtherCompaniesAreNotAffected()
    {
        var limited = MessagingHostOf(_factory, "messaging-tenant-rate-limit", new()
        {
            ["Messaging:SendRateLimitPerMinute"] = "100",
            ["Messaging:TenantSendRateLimitPerMinute"] = "2",
        });
        using var f = await StartChannelAsync(_factory, _client);
        using var other = await StartChannelAsync(_factory, _client);
        using var managerActor = await f.Tenant.AddMemberAsync("sender", Permission.ConversationsManage);
        using var thirdActor = await f.Tenant.AddMemberAsync("third", Permission.ConversationsManage);
        using var owner = limited.ClientOf(f.Tenant.Owner);
        using var manager = limited.ClientOf(managerActor);
        using var third = limited.ClientOf(thirdActor);
        using var otherOwner = limited.ClientOf(other.Tenant.Owner);
        await SetCredentialsAsync(owner, f.Channel.Id);
        await SetCredentialsAsync(otherOwner, other.Channel.Id);
        var conversation = await SeedConversationAsync(_factory.Services, f, DateTime.UtcNow.AddMinutes(-1), [In(0)]);
        var otherConversation = await SeedConversationAsync(_factory.Services, other, DateTime.UtcNow.AddMinutes(-1), [In(0)]);

        Assert.Equal(HttpStatusCode.Created, (await SendMessageAsync(owner, conversation.ConversationId)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await SendMessageAsync(manager, conversation.ConversationId)).StatusCode);
        var refused = await SendMessageAsync(third, conversation.ConversationId); // a user who has sent nothing yet

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.NotNull(refused.Headers.RetryAfter);
        Assert.Equal("application/problem+json", refused.Content.Headers.ContentType?.MediaType);
        Assert.Equal(2, limited.Sender.SentBy(f.Channel.Id).Count);
        Assert.Equal(HttpStatusCode.Created, (await SendMessageAsync(otherOwner, otherConversation.ConversationId)).StatusCode);
    }

    // ---- A company that is not Active --------------------------------------------------------------------------------

    [Fact]
    public async Task ASuspendedCompany_CanNeitherSendNorChangeCredentials()
    {
        var (f, conversation, owner, _) = await StartConversationAsync();
        using var _ = f;
        using var admin = await StartAdminAsync(_factory, _client);
        var before = await LoadChannelAsync(_factory.Services, f.Channel.Id);
        Assert.Equal(HttpStatusCode.OK, (await admin.Client.PutAsJsonAsync($"{AdminTenantsUrl}/{f.TenantId}/status", new { status = "Suspended" })).StatusCode);

        var responses = new[]
        {
            await SendMessageAsync(owner, conversation.ConversationId),
            await PutCredentialsAsync(owner, f.Channel.Id, "another-key"),
            await owner.DeleteAsync(CredentialsUrl(f.Channel.Id)),
        };

        foreach (var response in responses)
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("tenant_not_active", (await ReadProblemCodeAsync(response)).Code);
        }

        Assert.Empty(_host.Sender.SentBy(f.Channel.Id));
        Assert.Equal(before.ProviderApiKeyCiphertext, (await LoadChannelAsync(_factory.Services, f.Channel.Id)).ProviderApiKeyCiphertext);
        Assert.DoesNotContain((await SnapshotAsync(_factory.Services, f.TenantId)).Messages, m => m.Direction == MessageDirection.Outbound);
    }

    [Fact]
    public async Task APendingCompany_CannotSend()
    {
        using var tenant = await StartTenantAsync(_factory, _client, "pending", pending: true);
        using var owner = _host.ClientOf(tenant.Owner);

        var response = await SendMessageAsync(owner, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("tenant_not_active", (await ReadProblemCodeAsync(response)).Code);
    }

    // ---- The record step ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task WhenTheRecordCannotGetItsLockInTime_TheAnswerIsSentNotRecorded_NotAHangingRequest()
    {
        var bounded = MessagingHostOf(_factory, "messaging-record-timeout", new() { ["Messaging:RecordTimeoutSeconds"] = "1" });
        using var f = await StartChannelAsync(_factory, _client);
        using var owner = bounded.ClientOf(f.Tenant.Owner);
        await SetCredentialsAsync(owner, f.Channel.Id);
        var conversation = await SeedConversationAsync(_factory.Services, f, DateTime.UtcNow.AddMinutes(-1), [In(0)]);

        // Another session holds the tenant's ingestion lock, as a stuck delivery would.
        await using var holder = new NpgsqlConnection(_factory.ServerConnectionString);
        await holder.OpenAsync();
        await using var transaction = await holder.BeginTransactionAsync();
        await using (var take = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@key, 0))", holder, transaction))
        {
            take.Parameters.AddWithValue("key", f.TenantId.ToString());
            await take.ExecuteNonQueryAsync();
        }

        var started = System.Diagnostics.Stopwatch.StartNew();
        var response = await SendMessageAsync(owner, conversation.ConversationId);
        started.Stop();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var (code, body) = await ReadProblemCodeAsync(response);
        Assert.Equal("message_sent_not_recorded", code);
        AssertNoInternalsLeaked(body);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(20), $"The request took {started.Elapsed}.");
        Assert.Single(bounded.Sender.SentBy(f.Channel.Id)); // the provider did get it
        await transaction.RollbackAsync();
        Assert.DoesNotContain((await SnapshotAsync(_factory.Services, f.TenantId)).Messages, m => m.Direction == MessageDirection.Outbound);

        // Once the lock is free the next message is recorded normally.
        Assert.Equal(HttpStatusCode.Created, (await SendMessageAsync(owner, conversation.ConversationId)).StatusCode);
    }

    [Fact]
    public async Task SendsAndWebhookIngestionOfTheSameConversation_RunConcurrently_AndEveryMessageIsStoredOnce()
    {
        const int sends = 6;
        const int deliveries = 6;
        using var f = await StartChannelAsync(_factory, _client);
        using var owner = _host.ClientOf(f.Tenant.Owner);
        await SetCredentialsAsync(owner, f.Channel.Id);
        await ClearQueueAsync(_factory.Services);
        var runner = RunnerOf(_factory.Services);
        var waId = UniqueWaId();
        await DeliverAsync(_client, f, Dialog360Samples.InboundText($"wamid.IN-{Guid.NewGuid():N}", waId: waId));
        await DrainAsync(runner);
        var conversationId = Assert.Single((await SnapshotAsync(_factory.Services, f.TenantId)).Conversations).Id;

        // The customer keeps writing (each delivery a message of its own, a second apart) while a person sends.
        for (var i = 1; i <= deliveries; i++)
        {
            await DeliverAsync(_client, f, Dialog360Samples.InboundText($"wamid.IN-{Guid.NewGuid():N}", timestamp: Dialog360Samples.T0 + i, waId: waId));
        }

        var sending = Enumerable.Range(0, sends).Select(i => Task.Run(() => SendMessageAsync(owner, conversationId, $"Respuesta {i}"))).ToList();
        var ingesting = Enumerable.Range(0, 3).Select(_ => Task.Run(() => DrainAsync(runner))).ToList();
        var responses = await Task.WhenAll(sending).WaitAsync(TimeSpan.FromSeconds(60));
        await Task.WhenAll(ingesting).WaitAsync(TimeSpan.FromSeconds(60));

        // A delivery that found the tenant busy was deferred, not lost: make it due and process it.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.Database.ExecuteSqlAsync($"""UPDATE "WebhookEvents" SET "NextAttemptAt" = now() - interval '1 second' WHERE "TenantId" = {f.TenantId} AND "Status" = 'Pending'""");
        }

        await DrainAsync(runner);

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var snapshot = await SnapshotAsync(_factory.Services, f.TenantId);
        Assert.All(snapshot.Events, e => Assert.Equal(WebhookEventStatus.Processed, e.Status));
        Assert.Equal(sends, snapshot.Messages.Count(m => m.Direction == MessageDirection.Outbound));
        Assert.Equal(deliveries + 1, snapshot.Messages.Count(m => m.Direction == MessageDirection.Inbound));
        Assert.Equal(snapshot.Messages.Count, snapshot.Messages.Select(m => m.ProviderMessageId).Distinct().Count());
        var conversation = Assert.Single(snapshot.Conversations);
        Assert.Equal(snapshot.Messages.Max(m => m.SentAt), conversation.LastMessageAt);
        Assert.Equal(snapshot.Messages.Min(m => m.SentAt), conversation.StartedAt);
        Assert.Equal(snapshot.Messages.Where(m => m.Direction == MessageDirection.Outbound).Min(m => m.SentAt), conversation.FirstResponseAt);
    }
}
