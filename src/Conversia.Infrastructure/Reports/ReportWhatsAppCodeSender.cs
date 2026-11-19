using Conversia.Application.Messaging;
using Conversia.Application.Reports;
using Conversia.Domain.Enums;
using Conversia.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Conversia.Infrastructure.Reports;

/// <summary>
/// Sends the verification code of a WhatsApp opt-in (docs/REPORTS.md, "WhatsApp opt-in") through the company's own line, with the template of one of
/// its report definitions (or, without one, its first approved utility template with three fields): the first field says what it is, the second
/// carries the code, the third links to the opt-in page. A company in sandbox mode sends through the internal sender. Runs in the caller's company.
/// </summary>
public sealed class ReportWhatsAppCodeSender(
    ApplicationDbContext db, IReportStore store, IWhatsAppMessageSenderResolver resolver, ReportSettings settings, ILogger<ReportWhatsAppCodeSender> logger)
    : IReportWhatsAppCodeSender
{
    public async Task<bool> SendAsync(Guid tenantId, string recipientWaId, string code, CancellationToken cancellationToken = default)
    {
        var candidates = await db.ReportDefinitions.AsNoTracking()
            .Where(d => d.WhatsAppChannelId != null && d.WhatsAppTemplateName != null)
            .OrderByDescending(d => d.IsActive).ThenBy(d => d.CreatedAt)
            .Select(d => new { ChannelId = d.WhatsAppChannelId!.Value, Name = d.WhatsAppTemplateName!, Language = d.WhatsAppTemplateLanguage! })
            .ToListAsync(cancellationToken);
        var options = await store.ListTemplateOptionsAsync(cancellationToken); // approved, utility, three fields, active line
        var choice = candidates.Select(c => options.FirstOrDefault(o => o.ChannelId == c.ChannelId && o.Name == c.Name && o.Language == c.Language)).FirstOrDefault(o => o is not null)
            ?? options.FirstOrDefault();
        if (choice is null)
        {
            return false;
        }

        var channel = await db.WhatsAppChannels.AsNoTracking().FirstAsync(c => c.Id == choice.ChannelId, cancellationToken);
        var template = await db.MessageTemplates.AsNoTracking().FirstAsync(t => t.ChannelId == choice.ChannelId && t.Name == choice.Name && t.Language == choice.Language, cancellationToken);
        var sandbox = await db.Tenants.AsNoTracking().Where(t => t.Id == tenantId).Select(t => t.SandboxMode).FirstAsync(cancellationToken);
        if (!sandbox && !channel.HasProviderApiKey)
        {
            return false;
        }

        string[] values = ["verificación de tu número para los informes", $"tu código es {code}", new Uri(settings.WebBaseUrl, "informes/whatsapp").ToString()];
        var parameters = template.ParameterNames.Select((name, i) => new WhatsAppTemplateParameter(template.NamedParameters ? name : null, values[i])).ToList();
        var result = await resolver.Resolve(channel, sandbox).SendTemplateAsync(
            new WhatsAppTemplateMessage(channel, recipientWaId, template.Name, template.Language, parameters), cancellationToken);
        if (result.Status != WhatsAppSendStatus.Sent)
        {
            logger.LogWarning(ReportService.ReportTrail, "The WhatsApp verification code of tenant {TenantId} was not sent: {Status}.", tenantId, result.Status);
        }

        return result.Status == WhatsAppSendStatus.Sent;
    }
}
