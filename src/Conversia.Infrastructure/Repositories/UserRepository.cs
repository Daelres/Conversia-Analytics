using Conversia.Application.Exceptions;
using Conversia.Domain.Entities;
using Conversia.Domain.Interfaces;
using Conversia.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Conversia.Infrastructure.Repositories;

public class UserRepository : IUserRepository
{
    private readonly ApplicationDbContext _dbContext;

    public UserRepository(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    // Email is globally unique across tenants (docs/USERS_TENANTS_PERMISSIONS.md,
    // "Login and tenant resolution"), so these two lookups must ignore the
    // phase-2 tenant-scoped query filter on Users — they run before a tenant
    // context even exists (register-tenant) or independently of one (login).
    public Task<User?> GetByEmailAsync(string email, CancellationToken cancellationToken = default)
        => _dbContext.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Email == email, cancellationToken);

    public Task<bool> ExistsByEmailAsync(string email, CancellationToken cancellationToken = default)
        => _dbContext.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == email, cancellationToken);

    // Deliberately tenant-scoped (does not IgnoreQueryFilters): a lookup by
    // id for a user in another tenant returns null, the same as a
    // nonexistent id — see docs/USERS_TENANTS_PERMISSIONS.md's
    // anti-enumeration contract for cross-tenant targets.
    public Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => _dbContext.Users.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);

    public async Task<IReadOnlyList<User>> ListByTenantAsync(Guid tenantId, CancellationToken cancellationToken = default)
        => await _dbContext.Users.Where(u => u.TenantId == tenantId).ToListAsync(cancellationToken);

    public async Task AddAsync(User user, CancellationToken cancellationToken = default)
    {
        await _dbContext.Users.AddAsync(user, cancellationToken);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            // The pre-check (ExistsByEmailAsync) is racy under concurrent
            // registrations; the unique index is the real source of truth.
            throw new DuplicateEmailException();
        }
    }
}
