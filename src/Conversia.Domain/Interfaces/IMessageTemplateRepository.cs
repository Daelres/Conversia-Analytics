using Conversia.Domain.Entities;
using Conversia.Domain.Enums;

namespace Conversia.Domain.Interfaces;

/// <summary>The copies of the channels' templates (docs/MESSAGING.md, "Templates"). Scoped to the current tenant like every other repository.</summary>
public interface IMessageTemplateRepository
{
    /// <summary>Untracked; null when the channel has no template of that name in that language.</summary>
    Task<MessageTemplate?> FindAsync(Guid channelId, string name, string language, CancellationToken cancellationToken = default);

    /// <summary>Untracked, by name then language.</summary>
    Task<IReadOnlyList<MessageTemplate>> ListByChannelAsync(Guid channelId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the channel's copy with what the provider listed, in one transaction under the channel's lock: a template the provider still
    /// lists is refreshed (same row; a status notification newer than <paramref name="listedAt"/> keeps its status), a new one is added,
    /// and one it no longer lists is removed, with two guards (docs/MESSAGING.md, "Templates"): an empty list never removes anything, and a
    /// list that is not <paramref name="complete"/> may not remove more than half of the stored templates. Held removals are reported.
    /// </summary>
    Task<TemplateReplaceResult> ReplaceForChannelAsync(
        Guid tenantId, Guid channelId, IReadOnlyList<TemplateSnapshot> snapshots, bool complete, DateTime syncedAt, DateTime listedAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// A status notification of the provider for one template of the channel, under the channel's lock (so a sync cannot interleave). False
    /// when the channel has no such template or the notification is older than the last one applied.
    /// </summary>
    Task<bool> ChangeStatusAsync(Guid channelId, string name, string language, TemplateStatus status, DateTime changedAt, CancellationToken cancellationToken = default);
}

/// <summary>A template as the provider lists it, before it is stored.</summary>
public sealed record TemplateSnapshot(string Name, string Language, TemplateCategory Category, TemplateStatus Status, string? BodyText, bool Supported);

/// <param name="RemovalsHeld">Templates that the list no longer had but were kept by a guard (an empty or incomplete list).</param>
public sealed record TemplateReplaceResult(int Added, int Refreshed, int Removed, int RemovalsHeld = 0);
