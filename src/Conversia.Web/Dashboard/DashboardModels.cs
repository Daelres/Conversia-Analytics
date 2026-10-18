using Conversia.Application.DTOs.Metrics;
using Conversia.Application.DTOs.Organization;

namespace Conversia.Web.Dashboard;

/// <summary>Everything the dashboard shows for one period and branch, as the API computed it.</summary>
/// <param name="Previous">The conversion of the period right before, for the comparison; null when that call failed (the comparison is then left out, the page is not).</param>
/// <param name="Branches">For the branch filter; null when the caller may not see the organization.</param>
public sealed record DashboardData(
    DateRange Range,
    MetricsPeriod Period,
    SummaryResponse Summary,
    ConversionResponse Conversion,
    FirstResponseResponse FirstResponse,
    VolumeResponse Volume,
    AlertsResponse Alerts,
    ConversionCounts? Previous,
    IReadOnlyList<BranchResponse>? Branches);
