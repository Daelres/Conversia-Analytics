extern alias ApiHost;

using System.Collections.Concurrent;
using System.Security.Cryptography;
using Conversia.Application.Messaging;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Conversia.Web.IntegrationTests.Infrastructure;

/// <summary>
/// Stands in for the provider, the same way the API's own integration tests do. What it answers is set per channel, so tests that
/// share the host do not disturb each other.
/// </summary>
internal sealed class FakeMessageSender : IWhatsAppMessageSender, IWhatsAppMessageSenderResolver
{
    private readonly ConcurrentDictionary<Guid, Func<WhatsAppSendResult>> _answers = new();
    private readonly ConcurrentQueue<(Guid ChannelId, string RecipientWaId, string Text)> _sent = new();

    public void Answer(Guid channelId, Func<WhatsAppSendResult> answer) => _answers[channelId] = answer;

    public IReadOnlyList<(Guid ChannelId, string RecipientWaId, string Text)> SentBy(Guid channelId) => _sent.Where(s => s.ChannelId == channelId).ToList();

    // A company in sandbox mode never reaches the provider, so it never reaches its stand-in either: the real internal sender answers.
    public IWhatsAppMessageSender Resolve(WhatsAppChannel channel, bool sandboxMode) => sandboxMode ? SandboxSender : this;

    private static readonly Conversia.Infrastructure.Messaging.SandboxMessageSender SandboxSender = new();

    public Task<WhatsAppSendResult> SendTextAsync(WhatsAppTextMessage message, CancellationToken cancellationToken = default)
    {
        _sent.Enqueue((message.Channel.Id, message.RecipientWaId, message.Text));
        return Task.FromResult(_answers.TryGetValue(message.Channel.Id, out var answer) ? answer() : WhatsAppSendResult.Sent($"wamid.FAKE-{Guid.NewGuid():N}"));
    }
}

/// <summary>The real API with a master key and a stand-in provider, plus what a test needs to put a conversation in its database.</summary>
internal sealed record MessagingApiHost(WebApplicationFactory<ApiHost::Program> Api, FakeMessageSender Sender)
{
    private static long _waIdCounter = 5_739_000_0000L;

    public static MessagingApiHost Create(TestApiFactory factory)
    {
        var sender = new FakeMessageSender();
        var api = factory.WithWebHostBuilder(builder =>
        {
            // A throw-away master key, new on every run.
            builder.UseSetting("Messaging:CredentialEncryptionKey", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IWhatsAppMessageSenderResolver>();
                services.AddSingleton<IWhatsAppMessageSenderResolver>(sender);
            });
        });
        return new MessagingApiHost(api, sender);
    }

    /// <summary>The stored channel, bypassing the tenant filter.</summary>
    public async Task<WhatsAppChannel> LoadChannelAsync(Guid channelId)
    {
        using var scope = Api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.WhatsAppChannels.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == channelId);
    }

    /// <summary>An open conversation on the channel in which a customer (a dummy number) wrote a minute ago. Returns its id and the customer's number.</summary>
    public async Task<(Guid ConversationId, string WaId)> SeedConversationAsync(Guid channelId, bool closed = false)
    {
        var channel = await LoadChannelAsync(channelId);
        using var scope = Api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var startedAt = DateTime.UtcNow.AddMinutes(-1);
        var waId = Interlocked.Increment(ref _waIdCounter).ToString();
        var contact = Contact.Create(channel.TenantId, waId, "Cliente", startedAt);
        var conversation = Conversation.Start(channel.TenantId, contact.Id, channel.Id, channel.BranchId, startedAt, ConversationSource.Organic);
        var opportunity = Opportunity.Open(channel.TenantId, conversation.Id);
        var message = Message.Create(channel.TenantId, conversation.Id, channel.Id, $"wamid.{Guid.NewGuid():N}", MessageDirection.Inbound, "text", "Hola, quiero información", startedAt);
        conversation.ApplyTimeline(ConversationTimeline.From([(message.Direction, message.SentAt)])!);
        if (closed)
        {
            conversation.Close(conversation.LastMessageAt);
        }

        db.AddRange(contact, conversation, opportunity, message);
        await db.SaveChangesAsync();
        return (conversation.Id, waId);
    }
}
