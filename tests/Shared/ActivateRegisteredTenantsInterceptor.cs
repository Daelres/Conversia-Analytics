using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Conversia.Tests.Shared;

/// <summary>
/// TEST HOSTS ONLY. A tenant registers as Pending, and almost every test is about something else (branches, webhooks, metrics) and needs
/// a company that operates. This makes each tenant a test registers Active with a generous quota at the moment it is saved, so the real
/// registration endpoint is still used. A tenant whose name starts with <see cref="PendingPrefix"/> is left as it registered, for the
/// tests of the approval flow. Nothing like this exists in the product code.
/// </summary>
public sealed class ActivateRegisteredTenantsInterceptor : SaveChangesInterceptor
{
    public const string PendingPrefix = "PENDING ";

    public const int Quota = 50;

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Activate(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Activate(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private static void Activate(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        foreach (var entry in context.ChangeTracker.Entries<Tenant>().Where(e => e.State == EntityState.Added && !e.Entity.Name.StartsWith(PendingPrefix, StringComparison.Ordinal)))
        {
            entry.Property(nameof(Tenant.Status)).CurrentValue = TenantStatus.Active;
            entry.Property(nameof(Tenant.MaxActiveLines)).CurrentValue = Quota;
        }
    }
}
