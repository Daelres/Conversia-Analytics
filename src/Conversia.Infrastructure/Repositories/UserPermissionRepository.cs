using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Domain.Interfaces;
using Conversia.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Conversia.Infrastructure.Repositories;

public class UserPermissionRepository : IUserPermissionRepository
{
    private readonly ApplicationDbContext _dbContext;

    public UserPermissionRepository(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    // Tenant scoping is enforced by ApplicationDbContext's own global query
    // filter on UserPermissions — no explicit tenant check needed here.
    public Task<bool> HasPermissionAsync(Guid userId, Permission permission, CancellationToken cancellationToken = default)
        => _dbContext.UserPermissions.AnyAsync(up => up.UserId == userId && up.Permission == permission, cancellationToken);

    public async Task<IReadOnlyList<Permission>> ListByUserAsync(Guid userId, CancellationToken cancellationToken = default)
        => await _dbContext.UserPermissions
            .Where(up => up.UserId == userId)
            .Select(up => up.Permission)
            .ToListAsync(cancellationToken);

    public async Task AssignAsync(UserPermission userPermission, CancellationToken cancellationToken = default)
    {
        await _dbContext.UserPermissions.AddAsync(userPermission, cancellationToken);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            // The unique index (UserId, Permission) is the real source of
            // truth for a concurrent duplicate assignment — same pattern as
            // UserRepository's duplicate-email handling. Another request
            // already holds this exact assignment, so treat it as an
            // idempotent no-op rather than a failure.
        }
    }

    // Single set-based DELETE; bypasses the change tracker, so no entity is
    // loaded. The UserPermissions query filter still applies as a subquery.
    public Task RemoveAsync(Guid userId, Permission permission, CancellationToken cancellationToken = default)
        => _dbContext.UserPermissions
            .Where(up => up.UserId == userId && up.Permission == permission)
            .ExecuteDeleteAsync(cancellationToken);
}
