using Conversia.Application.Exceptions;
using Conversia.Application.Ingestion;
using Conversia.Application.Interfaces;
using Conversia.Domain.Enums;
using Conversia.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Conversia.Infrastructure.Messaging;

/// <summary>
/// The <c>Templates</c> settings: the periodic sync of every channel's templates (docs/MESSAGING.md, "Templates").
/// </summary>
public sealed record TemplateSyncOptions(bool Enabled, int IntervalMinutes)
{
    public const string SectionName = "Templates";
    public const string EnabledKey = SectionName + ":SyncEnabled";
    public const string IntervalMinutesKey = SectionName + ":SyncIntervalMinutes";

    public static TemplateSyncOptions Default { get; } = new(Enabled: true, IntervalMinutes: 360);

    public TimeSpan Interval => TimeSpan.FromMinutes(IntervalMinutes);
}

/// <summary>
/// Syncs the templates of every channel that can be synced: active, with a key, of an Active company that is not in sandbox mode. One
/// channel at a time, each in its own scope with its company set (<see cref="ITenantContext"/>), so a channel that fails (a revoked key,
/// a quota used up, an unreachable provider) is logged and the others go on. The first run waits one interval: a restart never sends a
/// burst of requests to 360dialog (whose sandbox keys allow 25 requests in total; a manual sync is the way to refresh at once).
/// </summary>
public sealed class TemplateSyncService(
    IServiceScopeFactory scopeFactory, TemplateSyncOptions options, TimeProvider timeProvider, ILogger<TemplateSyncService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Interval, timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RunOnceAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // The host is stopping.
        }
    }

    /// <summary>Syncs every eligible channel once. Never throws. Returns how many channels were synced.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        List<(Guid TenantId, Guid ChannelId)> channels;
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            // Across companies on purpose (a background job has no tenant); each channel is then synced inside its own company's scope.
            var rows = await (
                    from c in db.WhatsAppChannels.IgnoreQueryFilters().AsNoTracking()
                    join t in db.Tenants.AsNoTracking() on c.TenantId equals t.Id
                    where c.IsActive && c.ProviderApiKeyCiphertext != null && t.Status == TenantStatus.Active && !t.SandboxMode
                    orderby c.Id
                    select new { c.TenantId, c.Id })
                .ToListAsync(cancellationToken);
            channels = [.. rows.Select(r => (r.TenantId, r.Id))];
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError("Listing the channels whose templates to sync failed: {Error}", WebhookErrors.Describe(ex));
            return 0;
        }

        var synced = 0;
        foreach (var (tenantId, channelId) in channels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId);
                await scope.ServiceProvider.GetRequiredService<ITemplateService>().SyncAsync(channelId, cancellationToken);
                synced++;
            }
            catch (MessagingException ex)
            {
                logger.LogWarning("The templates of channel {ChannelId} were not synced: {Code}.", channelId, ex.Code);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("The templates of channel {ChannelId} were not synced: {Error}", channelId, WebhookErrors.Describe(ex));
            }
        }

        return synced;
    }
}
