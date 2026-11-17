using Conversia.Application.DTOs.Conversations;
using Conversia.Application.DTOs.Inbox;
using Conversia.Application.Exceptions;
using Conversia.Application.Interfaces;
using Conversia.Domain.Common;
using Conversia.Domain.Interfaces;

namespace Conversia.Application.Services;

/// <summary>
/// The inbox (docs/MESSAGING.md, "Inbox"): the open conversations of the company that a person should answer, by priority, and a cheap
/// delta for polling. Only the sandbox conversations while the company is in sandbox mode, only real ones otherwise (docs/SANDBOX.md).
/// </summary>
public sealed class InboxService(
    IInboxQueries queries, ITenantRepository tenants, IAdvisorRepository advisors, ICurrentTenantService currentTenant, TimeProvider timeProvider) : IInboxService
{
    /// <summary>
    /// How far back of the answer's time the cursor is put: a row changed by a transaction that committed after this answer read, but
    /// stamped before it, still comes in the next delta. The price is that an item can come twice; the client replaces it by id.
    /// </summary>
    public static readonly TimeSpan CursorOverlap = TimeSpan.FromSeconds(10);

    public async Task<InboxAnswer> GetAsync(InboxQuery query, Guid? actingUserId, string? knownVersion = null, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var sandboxMode = await tenants.GetSandboxModeAsync(currentTenant.TenantId, cancellationToken) ?? false;

        Guid? advisorId = null;
        if (query.Mine == true && actingUserId is { } userId)
        {
            advisorId = (await advisors.GetByUserIdAsync(userId, cancellationToken))?.Id;
        }

        // One cheap indexed query decides whether anything may have changed (docs/MESSAGING.md, "Inbox"). Only a full list is answered
        // "unchanged": a delta is already small, and its cursor must move.
        var token = await queries.GetChangeTokenAsync(sandboxMode, cancellationToken);
        var version = VersionOf(query, sandboxMode, advisorId, token);
        if (query.Since is null && knownVersion is not null && string.Equals(knownVersion, version, StringComparison.Ordinal))
        {
            return new InboxAnswer(version, null);
        }

        var response = new InboxResponse { Cursor = token.DatabaseNow - CursorOverlap, SandboxMode = sandboxMode };
        if (query.Mine == true && advisorId is null)
        {
            response.NoAdvisorLinked = true;
            return new InboxAnswer(version, response);
        }

        var since = query.Since is { } at ? DateTime.SpecifyKind(at.Kind == DateTimeKind.Local ? at.ToUniversalTime() : at, DateTimeKind.Utc) : (DateTime?)null;
        var filter = new InboxFilter(sandboxMode, advisorId, query.BranchId, query.Unassigned == true, query.AwaitingReply == true, since);
        var items = await queries.ListAsync(filter, InboxResponse.MaxItems + 1, cancellationToken);

        var more = items.Count > InboxResponse.MaxItems;
        response.Truncated = since is null && more;
        response.FullReloadRequired = since is not null && more;
        response.Items = [.. items.Take(InboxResponse.MaxItems)];
        foreach (var item in response.Items)
        {
            item.WindowClosesAt = CustomerServiceWindow.ClosesAt(item.LastInboundAt);
            item.WindowOpen = CustomerServiceWindow.IsOpen(item.LastInboundAt, now);
            item.WindowClosingSoon = CustomerServiceWindow.IsClosingSoon(item.LastInboundAt, now);
        }

        return new InboxAnswer(version, response);
    }

    // What the answer depends on: the filters, the mode, the caller's advisor, the company's last change and open count, and the minute (the
    // waiting times and the window badges move with the clock). Opaque: a hash, never readable.
    private static string VersionOf(InboxQuery query, bool sandboxMode, Guid? advisorId, InboxChangeToken token)
    {
        var text = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{sandboxMode}|{query.Mine}|{advisorId}|{query.BranchId}|{query.Unassigned}|{query.AwaitingReply}|{token.LastChange?.Ticks}|{token.OpenCount}|{token.DatabaseNow:yyyyMMddHHmm}");
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))[..32];
    }

    public async Task<ConversationVersionResponse> GetConversationVersionAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        var version = await queries.GetVersionAsync(conversationId, cancellationToken) ?? throw new ConversationNotFoundException();

        // The window closes with the clock, not with a stored change: the version carries it, so a page reloads its composer when it flips.
        version.WindowOpen = CustomerServiceWindow.IsOpen(version.LastInboundAt, timeProvider.GetUtcNow().UtcDateTime);
        version.Version = $"{version.Version}-{(version.WindowOpen ? "w" : "t")}";
        return version;
    }
}

/// <summary>The company's messaging settings: read receipts and the typing indicator (docs/MESSAGING.md, "Read receipts and typing").</summary>
/// <remarks>
/// There is no tenant-side audit table (like a contact suppression): the trail of a change is the structured log line
/// <see cref="ReadReceiptsChanged"/> (event 4301), with the company, the user and the new value.
/// </remarks>
public sealed class MessagingSettingsService(ITenantRepository tenants, ITenantUnitOfWork unitOfWork, Microsoft.Extensions.Logging.ILogger<MessagingSettingsService> logger)
    : IMessagingSettingsService
{
    public static readonly Microsoft.Extensions.Logging.EventId ReadReceiptsChanged = new(4301, nameof(ReadReceiptsChanged));

    public async Task<MessagingSettingsResponse> GetAsync(Guid tenantId, CancellationToken cancellationToken = default)
        => new() { ReadReceiptsEnabled = await tenants.GetReadReceiptsEnabledAsync(tenantId, cancellationToken) };

    public async Task<MessagingSettingsResponse> SetAsync(
        Guid tenantId, SetMessagingSettingsRequest request, Guid? actingUserId = null, CancellationToken cancellationToken = default)
    {
        await using var change = await unitOfWork.BeginAsync(tenantId, cancellationToken) ?? throw new TenantNotFoundException();
        if (change.Tenant.SetReadReceipts(request.ReadReceiptsEnabled ?? false))
        {
            await change.CommitAsync(cancellationToken);
            Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(
                logger, ReadReceiptsChanged, "Read receipts of tenant {TenantId} were switched to {Enabled} by user {UserId}.",
                tenantId, change.Tenant.ReadReceiptsEnabled, actingUserId);
        }

        return new MessagingSettingsResponse { ReadReceiptsEnabled = change.Tenant.ReadReceiptsEnabled };
    }
}
