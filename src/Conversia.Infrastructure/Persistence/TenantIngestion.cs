using System.Linq.Expressions;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;

namespace Conversia.Infrastructure.Persistence;

/// <summary>
/// Which companies receive and store their WhatsApp deliveries (<see cref="Tenant.AcceptsWebhooksIn"/>): Active, Paused, and Suspended for a
/// late payment (docs/PLATFORM_ADMIN.md, "Lifecycle of a company"). Both forms below are generated from the domain's one definition
/// (<see cref="Tenant.StatusesThatReceive"/>, <see cref="Tenant.ReceivingSuspensionCause"/>): the webhook lookup and the monitors use the
/// expression, the two claim statements (webhook worker, auto-closer) the SQL. The classifier stays on Active only.
/// </summary>
internal static class TenantIngestion
{
    private static readonly TenantStatus[] Always = [.. Tenant.StatusesThatReceive];

    /// <summary>For LINQ: <c>db.Tenants.Where(TenantIngestion.AcceptsWebhooks).Any(t => t.Id == ...)</c>.</summary>
    public static readonly Expression<Func<Tenant, bool>> AcceptsWebhooks = t =>
        Always.Contains(t.Status) || (t.Status == TenantStatus.Suspended && t.SuspensionCause == Tenant.ReceivingSuspensionCause);

    /// <summary>
    /// The same rule as an SQL condition over the <c>"Tenants"</c> row, for raw statements. Built only from enum names (stored by name),
    /// never from input, so it is safe to splice.
    /// </summary>
    public static readonly string AcceptsWebhooksSql =
        $"(\"Tenants\".\"Status\" IN ({string.Join(", ", Always.Select(s => $"'{s}'"))}) "
        + $"OR (\"Tenants\".\"Status\" = '{TenantStatus.Suspended}' AND \"Tenants\".\"SuspensionCause\" = '{Tenant.ReceivingSuspensionCause}'))";
}
