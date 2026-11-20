using System.ComponentModel.DataAnnotations;
using Conversia.Application.Exceptions;
using Conversia.Application.Time;
using Conversia.Application.Validation;
using Conversia.Domain.Entities;
using Conversia.Domain.Exceptions;
using Conversia.Domain.Interfaces;

namespace Conversia.Application.Assistant;

/// <summary>Body of the administrator's assistant switches: the new value and why (1 to 200 characters, audited).</summary>
public class SetAssistantSwitchRequest
{
    [Display(Name = "Asistente")]
    [Required(ErrorMessage = ValidationMessages.Required)]
    public bool? Enabled { get; set; }

    [Display(Name = "Motivo")]
    [TrimmedText(Tenant.AiChangeReasonMaxLength)]
    public string? Reason { get; set; }
}

/// <summary>One counter of the assistant's activity: real and sandbox apart.</summary>
public sealed record AssistantCountRow(string Metric, int Real, int Sandbox);

/// <summary>The administrator's view of the assistant (docs/PLATFORM_ADMIN.md, "Assistant"): switches and counts only, never a conversation.</summary>
public class AssistantAdminResponse
{
    public bool GloballyEnabled { get; set; }

    /// <summary>The company's kill switch; null in the platform view.</summary>
    public bool? CompanyBlocked { get; set; }

    /// <summary>The company switched it on (its owner accepted the terms); null in the platform view.</summary>
    public bool? CompanyEnabled { get; set; }

    /// <summary>Lines of the company with the assistant on; null in the platform view.</summary>
    public int? LinesWithAssistant { get; set; }

    /// <summary>Companies that switched it on; null in a company's view.</summary>
    public int? CompaniesEnabled { get; set; }

    /// <summary>Timers armed now, being answered now, and conversations pending human review.</summary>
    public int Waiting { get; set; }

    public int Generating { get; set; }

    public int PendingReview { get; set; }

    /// <summary>The counters of the last <see cref="Days"/> local days.</summary>
    public IReadOnlyList<AssistantCountRow> Counts { get; set; } = [];

    public int Days { get; set; }
}

/// <summary>Counts of the assistant's activity for the monitoring, one grouped statement each (10-second timeout like the rest).</summary>
public interface IAssistantMonitoringQueries
{
    Task<IReadOnlyList<AssistantCountRow>> CountsAsync(Guid? tenantId, DateOnly fromDay, CancellationToken cancellationToken = default);

    Task<(int Waiting, int Generating, int PendingReview)> BacklogAsync(Guid? tenantId, DateTime now, CancellationToken cancellationToken = default);

    /// <summary>Companies with the assistant on, or for one company: whether it is on and on how many lines.</summary>
    Task<(int CompaniesEnabled, bool CompanyEnabled, int Lines)> AdoptionAsync(Guid? tenantId, CancellationToken cancellationToken = default);
}

public interface IAssistantAdminService
{
    Task<AssistantAdminResponse> GetPlatformAsync(CancellationToken cancellationToken = default);

    Task<AssistantAdminResponse> GetCompanyAsync(Guid tenantId, CancellationToken cancellationToken = default);

    Task<AssistantAdminResponse> SetGlobalAsync(Guid adminId, SetAssistantSwitchRequest request, CancellationToken cancellationToken = default);

    /// <summary><paramref name="request"/>'s <c>Enabled</c> false blocks the company.</summary>
    Task<AssistantAdminResponse> SetCompanyAsync(Guid adminId, Guid tenantId, SetAssistantSwitchRequest request, CancellationToken cancellationToken = default);
}

/// <summary>The administrator's kill switches: one for every company (<see cref="PlatformSwitch.Assistant"/>) and one per company (<c>Tenants.AssistantBlocked</c>), both audited.</summary>
public sealed class AssistantAdminService(
    IPlatformSwitchStore switches, ITenantUnitOfWork unitOfWork, ITenantRepository tenants, IAssistantMonitoringQueries queries, TimeProvider time, TenantTimeZone zone) : IAssistantAdminService
{
    public const int Days = 7;

    public async Task<AssistantAdminResponse> GetPlatformAsync(CancellationToken cancellationToken = default)
    {
        var response = await CountsAsync(null, cancellationToken);
        response.CompaniesEnabled = (await queries.AdoptionAsync(null, cancellationToken)).CompaniesEnabled;
        return response;
    }

    public async Task<AssistantAdminResponse> GetCompanyAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var tenant = await tenants.GetByIdAsync(tenantId, cancellationToken) ?? throw new TenantNotFoundException();
        var response = await CountsAsync(tenantId, cancellationToken);
        var adoption = await queries.AdoptionAsync(tenantId, cancellationToken);
        response.CompanyBlocked = tenant.AssistantBlocked;
        response.CompanyEnabled = adoption.CompanyEnabled;
        response.LinesWithAssistant = adoption.Lines;
        return response;
    }

    public async Task<AssistantAdminResponse> SetGlobalAsync(Guid adminId, SetAssistantSwitchRequest request, CancellationToken cancellationToken = default)
    {
        var enabled = request.Enabled ?? throw new DomainException("Indica si el asistente queda activado o desactivado.");
        await switches.SetAsync(PlatformSwitch.Assistant, enabled, adminId, Tenant.LimitChangeReason(request.Reason), cancellationToken);
        return await GetPlatformAsync(cancellationToken);
    }

    public async Task<AssistantAdminResponse> SetCompanyAsync(Guid adminId, Guid tenantId, SetAssistantSwitchRequest request, CancellationToken cancellationToken = default)
    {
        var enabled = request.Enabled ?? throw new DomainException("Indica si el asistente queda activado o desactivado.");
        var reason = Tenant.LimitChangeReason(request.Reason);
        await using (var change = await unitOfWork.BeginAsync(tenantId, cancellationToken) ?? throw new TenantNotFoundException())
        {
            var previous = change.Tenant.AssistantBlocked;
            if (change.Tenant.SetAssistantBlocked(!enabled))
            {
                change.Record(PlatformAuditLog.Record(adminId, PlatformAuditLog.Actions.TenantAssistantBlockedChanged, tenantId, new Dictionary<string, object?>
                {
                    ["from"] = previous,
                    ["to"] = !enabled,
                    ["reason"] = reason,
                }));
                await change.CommitAsync(cancellationToken);
            }
        }

        return await GetCompanyAsync(tenantId, cancellationToken);
    }

    private async Task<AssistantAdminResponse> CountsAsync(Guid? tenantId, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var backlog = await queries.BacklogAsync(tenantId, now, cancellationToken);
        return new AssistantAdminResponse
        {
            GloballyEnabled = await switches.IsEnabledAsync(PlatformSwitch.Assistant, cancellationToken),
            Waiting = backlog.Waiting,
            Generating = backlog.Generating,
            PendingReview = backlog.PendingReview,
            Counts = await queries.CountsAsync(tenantId, zone.LocalDate(now).AddDays(-(Days - 1)), cancellationToken),
            Days = Days,
        };
    }
}
