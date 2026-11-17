using Conversia.Application.DTOs.Conversations;
using Conversia.Application.DTOs.Inbox;
using Conversia.Application.DTOs.Templates;

namespace Conversia.Application.Interfaces;

/// <summary>The templates of the caller's channels (docs/MESSAGING.md, "Templates"). Creation stays in the 360dialog Hub.</summary>
public interface ITemplateService
{
    /// <summary>The templates of a channel of the caller's company. Throws <see cref="Exceptions.ChannelNotFoundException"/> for another company's channel.</summary>
    Task<ChannelTemplatesResponse> ListForChannelAsync(Guid channelId, CancellationToken cancellationToken = default);

    /// <summary>The templates that can be offered in a conversation: those of its channel. Throws <see cref="Exceptions.ConversationNotFoundException"/>.</summary>
    Task<ChannelTemplatesResponse> ListForConversationAsync(Guid conversationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the channel's templates from the provider with the channel's key and replaces the stored copy. Throws
    /// <see cref="Exceptions.MessagingException"/> for a channel without a key, a refused key, the provider's quota or an unreachable provider.
    /// In sandbox mode nothing is read or stored.
    /// </summary>
    Task<TemplateSyncResponse> SyncAsync(Guid channelId, CancellationToken cancellationToken = default);
}

/// <summary>Read receipts and the typing indicator (docs/MESSAGING.md, "Read receipts and typing"). Nothing here ever throws for the provider.</summary>
public interface IConversationPresenceService
{
    /// <summary>Marks the customer's latest message as read on WhatsApp, if the company has it on and it was not marked yet.</summary>
    Task<PresenceResponse> MarkReadAsync(Guid conversationId, CancellationToken cancellationToken = default);

    /// <summary>Shows "escribiendo..." to the customer, if the company has it on, a reply is plausible and none was shown in the last 20 seconds.</summary>
    Task<PresenceResponse> SendTypingAsync(Guid conversationId, CancellationToken cancellationToken = default);
}

/// <summary>The inbox of the caller's company (docs/MESSAGING.md, "Inbox").</summary>
public interface IInboxService
{
    /// <param name="actingUserId">The caller: <c>mine</c> means the conversations of the advisor linked to this user.</param>
    /// <param name="knownVersion">
    /// The <see cref="InboxAnswer.Version"/> the caller already has (its <c>If-None-Match</c>). When it is still current the list is not
    /// built at all and <see cref="InboxAnswer.Response"/> is null: the poll cost one cheap indexed query.
    /// </param>
    Task<InboxAnswer> GetAsync(InboxQuery query, Guid? actingUserId, string? knownVersion = null, CancellationToken cancellationToken = default);

    /// <summary>Throws <see cref="Exceptions.ConversationNotFoundException"/>.</summary>
    Task<ConversationVersionResponse> GetConversationVersionAsync(Guid conversationId, CancellationToken cancellationToken = default);
}

/// <summary>The inbox's version (an opaque token) and, unless the caller's version is still current, its rows.</summary>
public sealed record InboxAnswer(string Version, InboxResponse? Response);

/// <summary>
/// What tells whether a company's inbox may have changed, read in one indexed query: the last change of any of its conversations (real or
/// sandbox), how many are open, and the database's own time (the cursor of the delta is database time, not the application's).
/// </summary>
public sealed record InboxChangeToken(DateTime? LastChange, int OpenCount, DateTime DatabaseNow);

/// <summary>The inbox filters, resolved: <see cref="AdvisorId"/> is the caller's advisor when they asked for their own conversations.</summary>
public sealed record InboxFilter(
    bool SandboxMode, Guid? AdvisorId = null, Guid? BranchId = null, bool Unassigned = false, bool AwaitingReplyOnly = false, DateTime? ChangedSince = null);

/// <summary>Read side of the inbox: one query per call, by indexes of <c>Conversations</c>; tenant-scoped by the query filters.</summary>
public interface IInboxQueries
{
    /// <summary>
    /// Without <see cref="InboxFilter.ChangedSince"/>: the open conversations, awaiting reply first (longest waiting first), then by the last
    /// message (newest first), at most <paramref name="take"/>. With it: those changed after it, open or not, newest change first, and
    /// <b>whatever their advisor, branch or reply state</b>: <see cref="InboxItem.InFilter"/> says whether each still matches the filters, so
    /// a client can remove one that was reassigned out of them. The window fields of the items are left for the caller, who knows the time.
    /// </summary>
    Task<IReadOnlyList<InboxItem>> ListAsync(InboxFilter filter, int take, CancellationToken cancellationToken = default);

    /// <summary>The change token of the current company's inbox, real or sandbox.</summary>
    Task<InboxChangeToken> GetChangeTokenAsync(bool sandboxMode, CancellationToken cancellationToken = default);

    Task<ConversationVersionResponse?> GetVersionAsync(Guid conversationId, CancellationToken cancellationToken = default);
}

/// <summary>The company's messaging settings. Changing them needs <c>OrganizationManage</c>.</summary>
public interface IMessagingSettingsService
{
    Task<MessagingSettingsResponse> GetAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <param name="actingUserId">Who changes it: recorded in the trail of the change.</param>
    Task<MessagingSettingsResponse> SetAsync(Guid tenantId, SetMessagingSettingsRequest request, Guid? actingUserId = null, CancellationToken cancellationToken = default);
}
