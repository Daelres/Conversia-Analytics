using Conversia.Application.DTOs.Templates;
using Conversia.Application.Exceptions;
using Conversia.Application.Interfaces;
using Conversia.Application.Messaging;
using Conversia.Application.Sandbox;
using Conversia.Domain.Entities;
using Conversia.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace Conversia.Application.Services;

/// <summary>
/// The templates of the caller's channels (docs/MESSAGING.md, "Templates"): a copy of what the provider lists, refreshed on demand and by
/// the periodic job. In sandbox mode (docs/SANDBOX.md) the fixed sandbox list stands in and nothing is read from the provider or stored.
/// </summary>
public sealed class TemplateService(
    IWhatsAppChannelRepository channels, IConversationRepository conversations, ITenantRepository tenants, IMessageTemplateRepository templates,
    IWhatsAppTemplateSource source, TimeProvider timeProvider, ILogger<TemplateService> logger) : ITemplateService
{
    public async Task<ChannelTemplatesResponse> ListForChannelAsync(Guid channelId, CancellationToken cancellationToken = default)
    {
        var channel = await channels.GetByIdAsync(channelId, cancellationToken) ?? throw new ChannelNotFoundException();
        return await ListAsync(channel, cancellationToken);
    }

    public async Task<ChannelTemplatesResponse> ListForConversationAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        var conversation = await conversations.GetByIdAsync(conversationId, cancellationToken) ?? throw new ConversationNotFoundException();
        var channel = await channels.GetByIdAsync(conversation.ChannelId, cancellationToken)
            ?? throw new InvalidOperationException("The channel of the conversation no longer exists.");
        return await ListAsync(channel, cancellationToken);
    }

    public async Task<TemplateSyncResponse> SyncAsync(Guid channelId, CancellationToken cancellationToken = default)
    {
        var channel = await channels.GetByIdAsync(channelId, cancellationToken) ?? throw new ChannelNotFoundException();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (await SandboxModeAsync(channel, cancellationToken))
        {
            return new TemplateSyncResponse { Sandbox = true, SyncedAt = now };
        }

        if (!channel.HasProviderApiKey)
        {
            throw new MessagingException(MessagingProblem.CredentialsMissing);
        }

        var listedAt = timeProvider.GetUtcNow().UtcDateTime; // before the read: a notification after it wins over the list's status
        var result = await source.ListAsync(channel, cancellationToken);
        if (result.Status != WhatsAppSendStatus.Sent)
        {
            // The stored copy stays as it was: a failed read says nothing about the templates.
            throw new MessagingException(result.Status switch
            {
                WhatsAppSendStatus.InvalidCredentials => MessagingProblem.InvalidCredentials,
                WhatsAppSendStatus.LimitReached => MessagingProblem.LimitReached,
                WhatsAppSendStatus.TransientFailure => MessagingProblem.ProviderUnavailable,
                _ => MessagingProblem.Rejected,
            });
        }

        var replaced = await templates.ReplaceForChannelAsync(
            channel.TenantId, channel.Id, result.Templates, result.Complete, timeProvider.GetUtcNow().UtcDateTime, listedAt, cancellationToken);
        if (replaced.RemovalsHeld > 0)
        {
            logger.LogWarning(
                "The template list of channel {ChannelId} was empty or incomplete: {Held} stored templates it did not list were kept.", channel.Id, replaced.RemovalsHeld);
        }

        logger.LogInformation(
            "Templates of channel {ChannelId} synced: {Added} added, {Refreshed} refreshed, {Removed} removed.", channel.Id, replaced.Added, replaced.Refreshed, replaced.Removed);
        return new TemplateSyncResponse
        {
            Added = replaced.Added, Refreshed = replaced.Refreshed, Removed = replaced.Removed, RemovalsHeld = replaced.RemovalsHeld, SyncedAt = now,
        };
    }

    private async Task<ChannelTemplatesResponse> ListAsync(WhatsAppChannel channel, CancellationToken cancellationToken)
    {
        if (await SandboxModeAsync(channel, cancellationToken))
        {
            var seeded = SandboxTemplates.For(channel, timeProvider.GetUtcNow().UtcDateTime);
            return new ChannelTemplatesResponse { Sandbox = true, Templates = [.. seeded.Select(MessageTemplateResponse.From)] };
        }

        var stored = await templates.ListByChannelAsync(channel.Id, cancellationToken);
        return new ChannelTemplatesResponse
        {
            Templates = [.. stored.Select(MessageTemplateResponse.From)],
            LastSyncedAt = stored.Count == 0 ? null : stored.Max(t => t.SyncedAt),
        };
    }

    private async Task<bool> SandboxModeAsync(WhatsAppChannel channel, CancellationToken cancellationToken)
        => await tenants.GetSandboxModeAsync(channel.TenantId, cancellationToken) ?? false;
}
