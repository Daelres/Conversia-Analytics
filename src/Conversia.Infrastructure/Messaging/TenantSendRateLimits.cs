using Conversia.Application.Ingestion;
using Conversia.Application.Interfaces;
using Conversia.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Conversia.Infrastructure.Messaging;

/// <summary>
/// The per-company send limits in memory, for the send limiter (which runs on every send and cannot wait for the database). The administrator's
/// change applies at once on the instance that made it (<see cref="Update"/>); every instance reloads all of them from <c>Tenants</c> every
/// <see cref="RefreshInterval"/> (a column read of the companies that have one), so another instance applies it within that time.
/// A reload whose read started before a local change never overwrites it: each change carries a version, and the changes newer than the
/// reload's start are applied again over what it read.
/// </summary>
public sealed class TenantSendRateLimits(IServiceScopeFactory scopeFactory) : ITenantSendRateLimits
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private readonly Dictionary<Guid, (int? Limit, long Version)> _localChanges = [];
    private Dictionary<Guid, int> _limits = [];
    private long _version;

    public int? OverrideFor(Guid tenantId) => Volatile.Read(ref _limits).TryGetValue(tenantId, out var limit) ? limit : null;

    public void Update(Guid tenantId, int? perMinute)
    {
        lock (_gate)
        {
            _localChanges[tenantId] = (perMinute, ++_version);
            var next = new Dictionary<Guid, int>(_limits);
            Apply(next, tenantId, perMinute);
            Volatile.Write(ref _limits, next);
        }
    }

    public Task ReloadAsync(CancellationToken cancellationToken = default) => ReloadAsync(ReadAsync, cancellationToken);

    internal async Task ReloadAsync(Func<CancellationToken, Task<IReadOnlyList<(Guid TenantId, int Limit)>>> read, CancellationToken cancellationToken = default)
    {
        long startedAt;
        lock (_gate)
        {
            startedAt = _version;
        }

        var rows = await read(cancellationToken); // a failure leaves everything as it was

        lock (_gate)
        {
            var next = rows.ToDictionary(r => r.TenantId, r => r.Limit);
            foreach (var (tenantId, change) in _localChanges.ToList())
            {
                if (change.Version > startedAt)
                {
                    Apply(next, tenantId, change.Limit); // changed after the read began: the read may not have seen it
                }
                else
                {
                    _localChanges.Remove(tenantId); // committed before the read began: the database already says it
                }
            }

            Volatile.Write(ref _limits, next);
        }
    }

    private static void Apply(Dictionary<Guid, int> limits, Guid tenantId, int? limit)
    {
        if (limit is { } value)
        {
            limits[tenantId] = value;
        }
        else
        {
            limits.Remove(tenantId);
        }
    }

    private async Task<IReadOnlyList<(Guid TenantId, int Limit)>> ReadAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var rows = await db.Tenants.AsNoTracking()
            .Where(t => t.SendRateLimitPerMinute != null)
            .Select(t => new { t.Id, Limit = t.SendRateLimitPerMinute!.Value })
            .ToListAsync(cancellationToken);
        return rows.Select(r => (r.Id, r.Limit)).ToList();
    }
}

/// <summary>Loads the per-company send limits at startup and every <see cref="TenantSendRateLimits.RefreshInterval"/>.</summary>
public sealed class TenantSendRateLimitRefresher(TenantSendRateLimits limits, TimeProvider timeProvider, ILogger<TenantSendRateLimitRefresher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        using var timer = new PeriodicTimer(TenantSendRateLimits.RefreshInterval, timeProvider);
        try
        {
            do
            {
                try
                {
                    await limits.ReloadAsync(stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    // The previous values stay in force.
                    logger.LogWarning("The per-company send limits could not be reloaded: {Error}", WebhookErrors.Describe(ex));
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // The host is stopping.
        }
    }
}
