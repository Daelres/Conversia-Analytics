using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Domain.Interfaces;
using Conversia.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Conversia.Infrastructure.Repositories;

public class TenantRepository : ITenantRepository
{
    private readonly ApplicationDbContext _dbContext;

    public TenantRepository(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<Tenant?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => _dbContext.Tenants.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

    public Task<TenantStatus?> GetStatusAsync(Guid id, CancellationToken cancellationToken = default)
        => _dbContext.Tenants.AsNoTracking().Where(t => t.Id == id).Select(t => (TenantStatus?)t.Status).FirstOrDefaultAsync(cancellationToken);

    public async Task AddAsync(Tenant tenant, CancellationToken cancellationToken = default)
    {
        // Not saved here: UserRepository.AddAsync commits this same scoped
        // DbContext together with the owner user, so tenant + owner creation
        // is one transaction (docs/USERS_TENANTS_PERMISSIONS.md register-tenant flow).
        await _dbContext.Tenants.AddAsync(tenant, cancellationToken);
    }
}
