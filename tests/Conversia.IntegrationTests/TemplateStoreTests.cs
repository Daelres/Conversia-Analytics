using Conversia.Application.Interfaces;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Domain.Interfaces;
using Conversia.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>
/// The channel's template copy on PostgreSQL (docs/MESSAGING.md, "Templates"): an empty or incomplete list never wipes it, and status
/// notifications are applied in the order they happened, whatever order they arrive in and whatever a sync does meanwhile.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class TemplateStoreTests(CustomWebApplicationFactory factory)
{
    private static readonly DateTime T0 = DateTime.UtcNow.AddHours(-1);

    private static TemplateSnapshot Snapshot(string name, TemplateStatus status = TemplateStatus.Approved)
        => new(name, "es", TemplateCategory.Utility, status, "Hola {{1}}", true);

    private async Task<T> InTenantAsync<T>(Guid tenantId, Func<IMessageTemplateRepository, Task<T>> action)
    {
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId);
        return await action(scope.ServiceProvider.GetRequiredService<IMessageTemplateRepository>());
    }

    private async Task<List<MessageTemplate>> StoredAsync(Guid channelId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.MessageTemplates.IgnoreQueryFilters().AsNoTracking().Where(t => t.ChannelId == channelId).OrderBy(t => t.Name).ToListAsync();
    }

    private async Task<ChannelFixture> StartWithTemplatesAsync(params string[] names)
    {
        var f = await StartChannelAsync(factory, factory.CreateClient());
        await InTenantAsync(f.TenantId, r => r.ReplaceForChannelAsync(f.TenantId, f.Channel.Id, [.. names.Select(n => Snapshot(n))], true, T0, T0));
        return f;
    }

    [Fact]
    public async Task AnEmptyList_NeverRemovesTheStoredTemplates_EvenIfTheProviderCallsItComplete()
    {
        using var f = await StartWithTemplatesAsync("uno", "dos");

        var result = await InTenantAsync(f.TenantId, r => r.ReplaceForChannelAsync(f.TenantId, f.Channel.Id, [], true, T0.AddMinutes(1), T0.AddMinutes(1)));

        Assert.Equal((0, 2), (result.Removed, result.RemovalsHeld));
        Assert.Equal(2, (await StoredAsync(f.Channel.Id)).Count);
    }

    [Fact]
    public async Task AListWhoseEntriesWereAllSkipped_RemovesNothing()
    {
        using var f = await StartWithTemplatesAsync("uno", "dos", "tres");

        // What the source answers when every entry was invalid: nothing usable, not complete.
        var result = await InTenantAsync(f.TenantId, r => r.ReplaceForChannelAsync(f.TenantId, f.Channel.Id, [], false, T0.AddMinutes(1), T0.AddMinutes(1)));

        Assert.Equal(3, result.RemovalsHeld);
        Assert.Equal(3, (await StoredAsync(f.Channel.Id)).Count);
    }

    [Fact]
    public async Task AnIncompleteList_MayNotRemoveMoreThanHalf_ButACompleteOneMay()
    {
        using var f = await StartWithTemplatesAsync("uno", "dos", "tres", "cuatro");

        var incomplete = await InTenantAsync(f.TenantId, r => r.ReplaceForChannelAsync(f.TenantId, f.Channel.Id, [Snapshot("uno")], false, T0.AddMinutes(1), T0.AddMinutes(1)));
        Assert.Equal((0, 3), (incomplete.Removed, incomplete.RemovalsHeld));
        Assert.Equal(4, (await StoredAsync(f.Channel.Id)).Count);

        var halfIncomplete = await InTenantAsync(f.TenantId, r => r.ReplaceForChannelAsync(f.TenantId, f.Channel.Id, [Snapshot("uno"), Snapshot("dos")], false, T0.AddMinutes(2), T0.AddMinutes(2)));
        Assert.Equal((2, 0), (halfIncomplete.Removed, halfIncomplete.RemovalsHeld)); // half is allowed

        var complete = await InTenantAsync(f.TenantId, r => r.ReplaceForChannelAsync(f.TenantId, f.Channel.Id, [Snapshot("dos")], true, T0.AddMinutes(3), T0.AddMinutes(3)));
        Assert.Equal((1, 0), (complete.Removed, complete.RemovalsHeld));
        Assert.Equal(["dos"], (await StoredAsync(f.Channel.Id)).Select(t => t.Name));
    }

    [Fact]
    public async Task AnApprovedNotificationDeliveredAfterALaterPause_DoesNotUnpause()
    {
        using var f = await StartWithTemplatesAsync("uno");

        Assert.True(await InTenantAsync(f.TenantId, r => r.ChangeStatusAsync(f.Channel.Id, "uno", "es", TemplateStatus.Paused, T0.AddMinutes(10))));
        Assert.False(await InTenantAsync(f.TenantId, r => r.ChangeStatusAsync(f.Channel.Id, "uno", "es", TemplateStatus.Approved, T0.AddMinutes(5))));

        Assert.Equal(TemplateStatus.Paused, Assert.Single(await StoredAsync(f.Channel.Id)).Status);
    }

    [Fact]
    public async Task ASyncWhoseListWasReadBeforeANotification_KeepsTheNotifiedStatus()
    {
        using var f = await StartWithTemplatesAsync("uno");
        var listedAt = T0.AddMinutes(10);

        // The webhook pauses the template after the sync read the list (still approved there), before the sync stores it.
        await InTenantAsync(f.TenantId, r => r.ChangeStatusAsync(f.Channel.Id, "uno", "es", TemplateStatus.Paused, listedAt.AddSeconds(5)));
        await InTenantAsync(f.TenantId, r => r.ReplaceForChannelAsync(f.TenantId, f.Channel.Id, [Snapshot("uno")], true, listedAt.AddSeconds(10), listedAt));

        Assert.Equal(TemplateStatus.Paused, Assert.Single(await StoredAsync(f.Channel.Id)).Status);
    }

    [Fact]
    public async Task ConcurrentSyncAndNotification_SerializeOnTheChannel_AndTheNewerStatusWins()
    {
        using var f = await StartWithTemplatesAsync("uno");
        var listedAt = T0.AddMinutes(20);

        await Task.WhenAll(
            InTenantAsync(f.TenantId, r => r.ReplaceForChannelAsync(f.TenantId, f.Channel.Id, [Snapshot("uno")], true, listedAt.AddSeconds(1), listedAt)),
            InTenantAsync(f.TenantId, r => r.ChangeStatusAsync(f.Channel.Id, "uno", "es", TemplateStatus.Disabled, listedAt.AddSeconds(30))));

        Assert.Equal(TemplateStatus.Disabled, Assert.Single(await StoredAsync(f.Channel.Id)).Status);
    }
}
