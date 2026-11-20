using Conversia.Application.DTOs;
using Conversia.Application.DTOs.Reports;
using Conversia.Domain.Entities;

namespace Conversia.Application.Reports;

/// <summary>A user of the company as the report settings need it: the id, the email (where the report goes) and the WhatsApp opt-in.</summary>
public sealed record ReportUserFact(Guid Id, string Email, bool WhatsAppOptedIn);

/// <summary>The company facts a report needs: its name, whether it is in sandbox mode, and whether the AI is on.</summary>
public sealed record ReportTenantFact(string Name, bool SandboxMode, bool AiEnabled);

/// <summary>A delivery of a stored report with the recipient's email (company members see who received it).</summary>
public sealed record ReportDeliveryRow(ReportDelivery Delivery, string Email);

/// <summary>
/// The data of the manager reports (docs/REPORTS.md). Every method is scoped to the current tenant by the query filters, except
/// <see cref="FindRecipientForUnsubscribeAsync"/> (the anonymous unsubscribe link names its row by id) and <see cref="GetTenantAsync"/>.
/// </summary>
public interface IReportStore
{
    Task<IReadOnlyList<ReportDefinition>> ListDefinitionsAsync(CancellationToken cancellationToken = default);

    /// <summary>Tracked, with its recipients.</summary>
    Task<ReportDefinition?> FindDefinitionAsync(Guid id, CancellationToken cancellationToken = default);

    Task<int> CountDefinitionsAsync(CancellationToken cancellationToken = default);

    void AddDefinition(ReportDefinition definition);

    /// <summary>Deletes the definition with its recipients, its reports and their deliveries.</summary>
    Task DeleteDefinitionAsync(ReportDefinition definition, CancellationToken cancellationToken = default);

    Task SaveChangesAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ReportUserFact>> ListUsersAsync(CancellationToken cancellationToken = default);

    /// <summary>Tracked: the opt-in is written on it.</summary>
    Task<User?> FindUserAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ReportBranchOption>> ListBranchesAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> ListAdvisorNamesAsync(CancellationToken cancellationToken = default);

    Task<ReportTenantFact?> GetTenantAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>The templates a report can use: approved, utility, sendable, with exactly three placeholders, on an active line of the company.</summary>
    Task<IReadOnlyList<ReportTemplateOption>> ListTemplateOptionsAsync(CancellationToken cancellationToken = default);

    Task<PagedResponse<ReportSummaryResponse>> ListReportsAsync(int page, int pageSize, CancellationToken cancellationToken = default);

    Task<(ManagerReport Report, IReadOnlyList<ReportDeliveryRow> Deliveries)?> GetReportAsync(Guid reportId, CancellationToken cancellationToken = default);

    /// <summary>Adds a row to the ledger of written reports (saved with the next <see cref="SaveChangesAsync"/>).</summary>
    void AddLedgerEntry(ReportLedgerEntry entry);

    /// <summary>The model was already called for this definition, frequency and period (a scheduled composition with model attempts).</summary>
    Task<bool> ModelAlreadyCalledAsync(Guid definitionId, Domain.Enums.ReportFrequency frequency, DateOnly periodFrom, CancellationToken cancellationToken = default);

    /// <summary>The company's previews written since <paramref name="sinceUtc"/>.</summary>
    Task<int> CountPreviewsSinceAsync(DateTime sinceUtc, CancellationToken cancellationToken = default);

    Task<bool> IsRecipientAsync(Guid definitionId, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Across companies, tracked: the recipient and the name of its definition.</summary>
    Task<(ReportRecipient Recipient, string DefinitionName)?> FindRecipientForUnsubscribeAsync(Guid recipientId, CancellationToken cancellationToken = default);
}
