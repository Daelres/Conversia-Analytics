using System.IdentityModel.Tokens.Jwt;
using Conversia.Application.Interfaces;
using Conversia.Domain.Common;
using Conversia.Domain.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Conversia.Infrastructure.Persistence;

public class ApplicationDbContext : DbContext
{
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private readonly ICurrentTenantService _currentTenantService;

    public ApplicationDbContext(
        DbContextOptions<ApplicationDbContext> options,
        ICurrentTenantService currentTenantService,
        IHttpContextAccessor? httpContextAccessor = null)
        : base(options)
    {
        _currentTenantService = currentTenantService;
        _httpContextAccessor = httpContextAccessor;
    }

    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<User> Users => Set<User>();

    public DbSet<UserPermission> UserPermissions => Set<UserPermission>();

    public DbSet<Product> Products => Set<Product>();

    /// <summary>Operations table: not tenant-filtered, see <c>ErrorLogConfiguration</c>.</summary>
    public DbSet<ErrorLog> ErrorLogs => Set<ErrorLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ApplicationDbContext).Assembly);

        // Case-insensitive text for the emails and SKUs (see UserConfiguration / ProductConfiguration); the first migration creates it.
        modelBuilder.HasPostgresExtension("citext");

        // Tenant isolation: every query against Users/UserPermissions/Products is
        // scoped to the caller's own tenant, resolved from the tenant_id JWT
        // claim via ICurrentTenantService. Tenants itself is never filtered
        // — docs/USERS_TENANTS_PERMISSIONS.md, "Multi-tenancy enforcement".
        // UserPermissions has no TenantId column of its own (see the docs'
        // 3-table schema), so its filter joins back to Users.
        modelBuilder.Entity<User>()
            .HasQueryFilter(u => u.TenantId == _currentTenantService.TenantId);

        modelBuilder.Entity<UserPermission>()
            .HasQueryFilter(up => Users.Any(u => u.Id == up.UserId && u.TenantId == _currentTenantService.TenantId));

        modelBuilder.Entity<Product>()
            .HasQueryFilter(p => p.TenantId == _currentTenantService.TenantId);

        base.OnModelCreating(modelBuilder);
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        ApplyAuditInformation();
        return base.SaveChangesAsync(cancellationToken);
    }

    private void ApplyAuditInformation()
    {
        var currentUserId = GetCurrentUserId();
        var now = DatabaseTime.Truncate(DateTime.UtcNow);

        foreach (var entry in ChangeTracker.Entries<BaseEntity>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Entity.CreatedAt = now;
                    entry.Entity.CreatedBy = currentUserId;
                    break;
                case EntityState.Modified:
                    entry.Entity.UpdatedAt = now;
                    entry.Entity.UpdatedBy = currentUserId;
                    break;
            }
        }
    }

    private Guid? GetCurrentUserId()
    {
        var subClaim = _httpContextAccessor?.HttpContext?.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        return Guid.TryParse(subClaim, out var userId) ? userId : null;
    }
}
