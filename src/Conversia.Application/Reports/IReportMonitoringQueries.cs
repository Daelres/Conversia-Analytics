using Conversia.Application.DTOs.Admin;

namespace Conversia.Application.Reports;

/// <summary>The administrator's content-free view of the manager reports (across companies).</summary>
public interface IReportMonitoringQueries
{
    Task<ReportMonitoringResponse> GetAsync(CancellationToken cancellationToken = default);
}
