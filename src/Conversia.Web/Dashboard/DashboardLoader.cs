using Conversia.Application.DTOs.Metrics;
using Conversia.Application.Time;
using Conversia.Web.Services;
using Conversia.Web.Time;

namespace Conversia.Web.Dashboard;

/// <summary>What a load produced: the data, or the first failed call (so the page can react to it like every other page does).</summary>
public sealed record DashboardLoad(DashboardData? Data, IApiOutcome? Failure)
{
    public bool Succeeded => Data is not null;
}

/// <summary>
/// Gathers the dashboard in one parallel round: the five metric answers for the period and branch, the conversion of the period right
/// before (for the comparison) and, when the caller may see the organization, the branches for the filter. The period is resolved in the
/// tenant's own time zone, so "últimos 7 días" means the same days for the Web and the API.
/// </summary>
public sealed class DashboardLoader(IApiClient api, TenantTimeZone timeZone, TimeProvider timeProvider)
{
    public async Task<DashboardLoad> LoadAsync(string token, PeriodPreset preset, Guid? branchId, CancellationToken cancellationToken = default)
    {
        var range = PeriodPresets.Resolve(preset, timeZone.Today(timeProvider));
        var query = QueryFor(range, branchId);

        var summary = api.GetMetricsSummaryAsync(token, query, cancellationToken);
        var conversion = api.GetMetricsConversionAsync(token, query, cancellationToken);
        var firstResponse = api.GetMetricsFirstResponseAsync(token, query, cancellationToken);
        var volume = api.GetMetricsVolumeAsync(token, query, cancellationToken);
        var alerts = api.GetMetricsAlertsAsync(token, query, cancellationToken);
        var previous = api.GetMetricsConversionAsync(token, QueryFor(range.Previous(), branchId), cancellationToken);
        var branches = api.GetBranchesAsync(token, cancellationToken);
        await Task.WhenAll(summary, conversion, firstResponse, volume, alerts, previous, branches);

        IApiOutcome[] required = [summary.Result, conversion.Result, firstResponse.Result, volume.Result, alerts.Result];
        var failed = required.FirstOrDefault(o => o.Status == ApiResultStatus.Unauthorized)
                     ?? required.FirstOrDefault(o => o.Status != ApiResultStatus.Success);
        if (failed is not null)
        {
            return new DashboardLoad(null, failed);
        }

        // The comparison and the branch filter are extras: when either is unavailable (or refused) the dashboard still stands.
        var data = new DashboardData(
            range,
            summary.Result.Value!.Period,
            summary.Result.Value!,
            conversion.Result.Value!,
            firstResponse.Result.Value!,
            volume.Result.Value!,
            alerts.Result.Value!,
            previous.Result.IsSuccess ? previous.Result.Value!.Totals : null,
            branches.Result.IsSuccess ? branches.Result.Value! : null);
        return new DashboardLoad(data, null);
    }

    private static MetricsQuery QueryFor(DateRange range, Guid? branchId)
        => new() { From = range.From, To = range.To, BranchId = branchId };
}
