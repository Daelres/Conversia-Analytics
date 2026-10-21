using Conversia.Application.DTOs.Tenants;
using Conversia.Application.Exceptions;
using Conversia.Application.Interfaces;
using Conversia.Domain.Interfaces;

namespace Conversia.Application.Services;

public class TenantService : ITenantService
{
    private readonly ITenantRepository _tenants;
    private readonly IWhatsAppChannelRepository _channels;

    public TenantService(ITenantRepository tenants, IWhatsAppChannelRepository channels)
    {
        _tenants = tenants;
        _channels = channels;
    }

    public async Task<TenantInfoResponse> GetCurrentAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var tenant = await _tenants.GetByIdAsync(tenantId, cancellationToken) ?? throw new TenantNotFoundException();
        var usage = await _channels.CountAsync(cancellationToken);

        return new TenantInfoResponse
        {
            Id = tenant.Id,
            Name = tenant.Name,
            Status = tenant.Status,
            MaxActiveLines = tenant.MaxActiveLines,
            ActiveLines = usage.Active,
            RegisteredLines = usage.Registered,
        };
    }
}
