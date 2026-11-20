namespace Conversia.Application.DTOs.Admin;

/// <summary>The manager reports of one company in a month, as counts and money only (no text, no recipient).</summary>
public sealed record ReportMonitoringCompanyResponse(
    Guid TenantId, string TenantName, int Generated, int ByModel, int ByTemplate, int Sent, int Failed, int Pending, decimal EstimatedUsd);

/// <summary>
/// <c>GET /api/admin/monitoring/reports</c> (docs/PLATFORM_ADMIN.md, "Manager reports"): reports generated, sent and failed per company this month
/// (America/Bogota), the share written by the model and its cost, and the deployment's email sender identity.
/// </summary>
/// <param name="Month">The month, <c>yyyy-MM</c> (America/Bogota).</param>
/// <param name="EmailProvider"><c>None</c>, <c>Smtp</c> or <c>Pickup</c>.</param>
/// <param name="EmailSender">The configured From address (platform configuration, validated at startup); null without a provider.</param>
public sealed record ReportMonitoringResponse(
    string Month, string EmailProvider, string? EmailSender, bool AiConfigured, ReportMonitoringCompanyResponse Totals, IReadOnlyList<ReportMonitoringCompanyResponse> Companies);
