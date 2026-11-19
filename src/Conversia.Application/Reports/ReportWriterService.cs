using Conversia.Application.Exceptions;
using Conversia.Application.Time;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;

namespace Conversia.Application.Reports;

/// <summary>A written report, not stored yet: its period, the pack it came from and who wrote it.</summary>
public sealed record ReportDraft(ReportPeriod Period, ReportDataPack Pack, ReportComposition Composition, bool IsSandbox, string Company);

/// <summary>
/// Writes the report of a definition for a period (docs/REPORTS.md, "Generation"): the data pack of the current tenant, then the composer
/// (model or template). Runs in a scope whose tenant is set (the request for a preview, the job for a scheduled report).
/// </summary>
public sealed class ReportWriterService(IReportStore store, ReportDataPackBuilder builder, ReportComposer composer, TenantTimeZone timeZone, TimeProvider time)
{
    public TimeZoneInfo Zone => timeZone.Zone;

    /// <summary>
    /// Writes the report and records it in the ledger at once (saved before this returns, so its cost counts even if storing the report fails).
    /// A scheduled period whose model was already called is written by the template.
    /// </summary>
    public async Task<ReportDraft> WriteAsync(
        Guid tenantId, ReportDefinition definition, ReportPeriod period, ReportCompositionKind kind = ReportCompositionKind.Scheduled,
        string? modelBlockedReason = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var tenant = await store.GetTenantAsync(tenantId, cancellationToken) ?? throw new ReportNotFoundException();
        var branches = await store.ListBranchesAsync(cancellationToken);
        var scope = definition.BranchIds.Length == 0
            ? []
            : branches.Where(b => definition.BranchIds.Contains(b.Id)).Select(b => (b.Id, b.Name)).ToList();
        if (definition.BranchIds.Length > 0 && scope.Count == 0)
        {
            // Every chosen branch is gone: the scope stays those ids (no conversation matches), never "all branches".
            scope = definition.BranchIds.Select(id => (id, "Sucursal no disponible")).ToList();
        }

        var pack = await builder.BuildAsync(new ReportPackRequest(tenant.Name, definition.Frequency, period, scope, definition.Sections), timeZone.Zone, cancellationToken);
        var inPack = pack.Names().ToHashSet(StringComparer.Ordinal);
        var outside = branches.Select(b => b.Name).Concat(await store.ListAdvisorNamesAsync(cancellationToken))
            .Where(name => !inPack.Contains(name)).Distinct(StringComparer.Ordinal).ToList();
        if (modelBlockedReason is null && kind == ReportCompositionKind.Scheduled
            && await store.ModelAlreadyCalledAsync(definition.Id, definition.Frequency, period.From, cancellationToken))
        {
            modelBlockedReason = ReportFallbackReasons.ModelAlreadyCalled;
        }

        var composition = await composer.ComposeAsync(tenantId, tenant.SandboxMode, pack, outside, cancellationToken, modelBlockedReason);
        var usage = composition.Usage;
        store.AddLedgerEntry(ReportLedgerEntry.Record(
            tenantId, definition.Id, kind, definition.Frequency, period.From, composition.Generator, composition.FallbackReason, composition.LlmAttempts,
            usage.InputTokens + usage.CacheReadTokens + usage.CacheWriteTokens, usage.OutputTokens, composition.EstimatedUsd, time.GetUtcNow().UtcDateTime));
        await store.SaveChangesAsync(CancellationToken.None); // the model's cost is recorded whatever happens next
        return new ReportDraft(period, pack, composition, tenant.SandboxMode, tenant.Name);
    }
}
