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

    /// <summary>Throws <see cref="Exceptions.TenantNotFoundException"/> or <see cref="Exceptions.TenantStatusConflictException"/>.</summary>
    Task<TenantSummaryResponse> ChangeStatusAsync(Guid adminId, Guid tenantId, ChangeTenantStatusRequest request, CancellationToken cancellationToken = default);

    /// <summary>Throws <see cref="Exceptions.TenantNotFoundException"/> or <see cref="Exceptions.LineQuotaBelowActiveException"/>.</summary>
    Task<TenantSummaryResponse> SetLineQuotaAsync(Guid adminId, Guid tenantId, SetLineQuotaRequest request, CancellationToken cancellationToken = default);

    /// <summary>Switches a line of the company off. Idempotent. Throws <see cref="Exceptions.TenantNotFoundException"/> or <see cref="Exceptions.ChannelNotFoundException"/>.</summary>
    Task<TenantLineResponse> DeactivateChannelAsync(Guid adminId, Guid tenantId, Guid channelId, CancellationToken cancellationToken = default);

    Task<PagedResponse<AuditEntryResponse>> ListAuditAsync(AdminAuditQuery query, CancellationToken cancellationToken = default);
}
