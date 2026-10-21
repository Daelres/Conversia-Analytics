using Conversia.Domain.Entities;
using Conversia.Domain.Interfaces;
using Conversia.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Conversia.Infrastructure.Repositories;

// PlatformAdmins is a platform-level table: it has no tenant query filter, and nothing but the admin login and the admin API uses this class.
public class PlatformAdminRepository : IPlatformAdminRepository
{
    private readonly ApplicationDbContext _dbContext;

    public PlatformAdminRepository(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<PlatformAdmin?> GetByEmailAsync(string email, CancellationToken cancellationToken = default)
        => _dbContext.PlatformAdmins.AsNoTracking().FirstOrDefaultAsync(a => a.Email == email, cancellationToken);

    public Task<PlatformAdmin?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => _dbContext.PlatformAdmins.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

    public Task<bool> IsActiveAsync(Guid id, CancellationToken cancellationToken = default)
        => _dbContext.PlatformAdmins.AsNoTracking().AnyAsync(a => a.Id == id && a.IsActive, cancellationToken);

    public async Task<bool> TryAddAsync(PlatformAdmin admin, CancellationToken cancellationToken = default)
    {
        await _dbContext.PlatformAdmins.AddAsync(admin, cancellationToken);
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            _dbContext.Entry(admin).State = EntityState.Detached;
            return false;
        }
    }
}
