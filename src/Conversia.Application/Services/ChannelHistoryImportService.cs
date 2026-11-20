using Conversia.Application.DTOs.Organization;
using Conversia.Application.Exceptions;
using Conversia.Domain.Exceptions;
using Conversia.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace Conversia.Application.Services;

/// <summary>The history import opt-in of a channel (docs/MESSAGING.md, "History import").</summary>
public interface IChannelHistoryImportService
{
    /// <summary>
    /// Turns the import on (with the legal basis confirmed, recording who and when) or off. The caller must be the company owner: the API
    /// checks it before calling. Throws <see cref="DomainException"/> when turning it on without the confirmation.
    /// </summary>
    Task<ChannelResponse> SetAsync(Guid channelId, SetHistoryImportRequest request, Guid ownerUserId, CancellationToken cancellationToken = default);
}

public sealed class ChannelHistoryImportService(IWhatsAppChannelRepository channels, TimeProvider time, ILogger<ChannelHistoryImportService> logger) : IChannelHistoryImportService
{
    public async Task<ChannelResponse> SetAsync(Guid channelId, SetHistoryImportRequest request, Guid ownerUserId, CancellationToken cancellationToken = default)
    {
        var enable = request.Enabled == true;
        if (enable && !request.LegalBasisConfirmed)
        {
            throw new DomainException(SetHistoryImportRequest.LegalBasisRuleMessage);
        }

        var channel = await channels.GetForUpdateAsync(channelId, cancellationToken) ?? throw new ChannelNotFoundException();
        if (enable)
        {
            channel.EnableHistoryImport(ownerUserId, time.GetUtcNow().UtcDateTime, request.AnalyzeWithAi);
        }
        else
        {
            channel.DisableHistoryImport();
        }

        await channels.UpdateAsync(channel, cancellationToken);

        // The trail of the consent (there is no tenant-side audit table, as for a contact suppression): ids and flags only.
        logger.LogInformation(
            new EventId(4302, "HistoryImportChanged"),
            "The history import of channel {ChannelId} of tenant {TenantId} was set to {Enabled} (AI analysis {Ai}) by the owner {UserId}; legal basis confirmed: {Confirmed}.",
            channel.Id, channel.TenantId, enable, channel.HistoryAiAnalysisEnabled, ownerUserId, request.LegalBasisConfirmed);
        return ChannelResponse.From(channel);
    }
}
