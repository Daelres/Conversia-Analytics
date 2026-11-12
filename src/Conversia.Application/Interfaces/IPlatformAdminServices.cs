using Conversia.Application.DTOs;
using Conversia.Application.DTOs.Admin;
using Conversia.Application.DTOs.Auth;

namespace Conversia.Application.Interfaces;

public interface IPlatformAdminAuthService
{
    /// <summary>Throws <see cref="Exceptions.InvalidCredentialsException"/> for an unknown email, a wrong password or a deactivated administrator, alike.</summary>
    Task<AdminAuthResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Issues the short-lived token of a platform administrator: its own audience and key, no tenant, never usable on a company's API.</summary>
public interface IPlatformAdminTokenService
{
    (string Token, DateTime ExpiresAt) Issue(Guid adminId);
}

/// <summary>What a platform administrator can do: approve companies, set their line quota, switch a line off, read the audit trail.</summary>
public interface IPlatformAdminService
{
    Task<PagedResponse<TenantSummaryResponse>> ListTenantsAsync(AdminTenantsQuery query, CancellationToken cancellationToken = default);

    /// <summary>Throws <see cref="Exceptions.TenantNotFoundException"/>.</summary>
    Task<TenantDetailResponse> GetTenantAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Throws <see cref="Exceptions.TenantNotFoundException"/>, <see cref="Exceptions.TenantStatusConflictException"/> or, to reactivate a
    /// company whose data deletion started, <see cref="Exceptions.TenantLifecycleConflictException"/>. Cancelling switches the lines off,
    /// releases the licences and schedules the deletion of the data (docs/PLATFORM_ADMIN.md, "Lifecycle of a company").
    /// </summary>
    Task<TenantSummaryResponse> ChangeStatusAsync(Guid adminId, Guid tenantId, ChangeTenantStatusRequest request, CancellationToken cancellationToken = default);

    /// <summary>Throws <see cref="Exceptions.TenantNotFoundException"/> or <see cref="Exceptions.LineQuotaBelowActiveException"/>.</summary>
    Task<TenantSummaryResponse> SetLineQuotaAsync(Guid adminId, Guid tenantId, SetLineQuotaRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Switches the internal sandbox of the company on or off (docs/SANDBOX.md). The reason is required. Idempotent: asking for what it
    /// already is writes nothing. Throws <see cref="Exceptions.TenantNotFoundException"/>.
    /// </summary>
    Task<TenantSummaryResponse> SetSandboxModeAsync(Guid adminId, Guid tenantId, SetSandboxModeRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Switches the AI on or off for the company (docs/CLASSIFICATION.md, "Usage and quota"). The reason is required; the terms version is
    /// required to switch it on. Audited as consent evidence. Idempotent. Throws <see cref="Exceptions.TenantNotFoundException"/>.
    /// </summary>
    Task<TenantSummaryResponse> SetAiEnabledAsync(Guid adminId, Guid tenantId, SetAiEnabledRequest request, CancellationToken cancellationToken = default);

    /// <summary>Sets the company's monthly AI quota (null: the default). Audited; the same value writes nothing.</summary>
    Task<TenantSummaryResponse> SetAiQuotaAsync(Guid adminId, Guid tenantId, SetAiQuotaRequest request, CancellationToken cancellationToken = default);

    /// <summary>Sets the company's send rate limit per minute (null: the default). Audited; the same value writes nothing.</summary>
    Task<TenantSummaryResponse> SetSendRateLimitAsync(Guid adminId, Guid tenantId, SetSendRateLimitRequest request, CancellationToken cancellationToken = default);

    /// <summary>Switches a line of the company off. Idempotent. Throws <see cref="Exceptions.TenantNotFoundException"/> or <see cref="Exceptions.ChannelNotFoundException"/>.</summary>
    Task<TenantLineResponse> DeactivateChannelAsync(Guid adminId, Guid tenantId, Guid channelId, CancellationToken cancellationToken = default);

    Task<PagedResponse<AuditEntryResponse>> ListAuditAsync(AdminAuditQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a cancelled company's data now (the deletion job does it in the background): the typed name must be the company's and the
    /// reason is required. Idempotent once started. Throws <see cref="Exceptions.TenantNotFoundException"/>,
    /// <see cref="Exceptions.TenantLifecycleConflictException"/> (not cancelled) or a domain error (name, reason).
    /// </summary>
    Task<TenantSummaryResponse> RequestDataDeletionAsync(Guid adminId, Guid tenantId, DeleteTenantDataRequest request, CancellationToken cancellationToken = default);

    /// <summary>The company's lifecycle history from the audit trail (status changes, data deletion), newest first.</summary>
    Task<PagedResponse<AuditEntryResponse>> ListLifecycleAsync(Guid tenantId, int page, int pageSize, CancellationToken cancellationToken = default);
}

/// <summary>The platform's line licence inventory (docs/PLATFORM_ADMIN.md, "Line licences"). Every change is audited.</summary>
public interface ILineLicenseService
{
    Task<PagedResponse<LineLicenseResponse>> ListAsync(AdminLicensesQuery query, CancellationToken cancellationToken = default);

    /// <summary>Throws <see cref="Exceptions.LineLicenseNotFoundException"/>.</summary>
    Task<LineLicenseResponse> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<LineLicenseResponse> CreateAsync(Guid adminId, CreateLineLicenseRequest request, CancellationToken cancellationToken = default);

    Task<LineLicenseResponse> AssignAsync(Guid adminId, Guid id, AssignLineLicenseRequest request, CancellationToken cancellationToken = default);

    Task<LineLicenseResponse> ReleaseAsync(Guid adminId, Guid id, CancellationToken cancellationToken = default);

    Task<LineLicenseResponse> RetireAsync(Guid adminId, Guid id, CancellationToken cancellationToken = default);
}
