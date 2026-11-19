namespace Conversia.Application.Reports;

/// <summary>
/// Sends the one-time code that proves a WhatsApp number is the person's (docs/REPORTS.md, "WhatsApp opt-in"), through the company's own line and
/// an approved report template (the code goes in its second field). False when the company has no line or template to send it with yet.
/// </summary>
public interface IReportWhatsAppCodeSender
{
    Task<bool> SendAsync(Guid tenantId, string recipientWaId, string code, CancellationToken cancellationToken = default);
}
