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

    public DbSet<Branch> Branches => Set<Branch>();

    public DbSet<Advisor> Advisors => Set<Advisor>();

    public DbSet<WhatsAppChannel> WhatsAppChannels => Set<WhatsAppChannel>();

    public DbSet<Contact> Contacts => Set<Contact>();

    public DbSet<Conversation> Conversations => Set<Conversation>();

    public DbSet<Message> Messages => Set<Message>();

    public DbSet<Opportunity> Opportunities => Set<Opportunity>();

    public DbSet<WebhookEvent> WebhookEvents => Set<WebhookEvent>();

    /// <summary>Platform-level: the people who run the platform. Not tenant-scoped (they belong to no company).</summary>
    public DbSet<PlatformAdmin> PlatformAdmins => Set<PlatformAdmin>();

    /// <summary>Platform-level trail of the administrators' changes. Names a tenant as data, so it is deliberately unfiltered.</summary>
    public DbSet<PlatformAuditLog> PlatformAuditLogs => Set<PlatformAuditLog>();

    /// <summary>Operations table: not tenant-filtered, see <c>ErrorLogConfiguration</c>.</summary>
    public DbSet<ErrorLog> ErrorLogs => Set<ErrorLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ApplicationDbContext).Assembly);

        // Case-insensitive text for the emails (see UserConfiguration); the first migration creates it.
        modelBuilder.HasPostgresExtension("citext");

        // Tenant isolation: every query against Users/UserPermissions is
        // scoped to the caller's own tenant, resolved from the tenant_id JWT
        // claim via ICurrentTenantService. Tenants itself is never filtered
        // — docs/USERS_TENANTS_PERMISSIONS.md, "Multi-tenancy enforcement".
        // UserPermissions has no TenantId column of its own (see the docs'
        // 3-table schema), so its filter joins back to Users.
        modelBuilder.Entity<User>()
            .HasQueryFilter(u => u.TenantId == _currentTenantService.TenantId);

        modelBuilder.Entity<UserPermission>()
            .HasQueryFilter(up => Users.Any(u => u.Id == up.UserId && u.TenantId == _currentTenantService.TenantId));

        // The Conversia entities carry their own TenantId and the same filter.
        modelBuilder.Entity<Branch>().HasQueryFilter(e => e.TenantId == _currentTenantService.TenantId);
        modelBuilder.Entity<Advisor>().HasQueryFilter(e => e.TenantId == _currentTenantService.TenantId);
        modelBuilder.Entity<WhatsAppChannel>().HasQueryFilter(e => e.TenantId == _currentTenantService.TenantId);
        modelBuilder.Entity<Contact>().HasQueryFilter(e => e.TenantId == _currentTenantService.TenantId);
        modelBuilder.Entity<Conversation>().HasQueryFilter(e => e.TenantId == _currentTenantService.TenantId);
        modelBuilder.Entity<Message>().HasQueryFilter(e => e.TenantId == _currentTenantService.TenantId);
        modelBuilder.Entity<Opportunity>().HasQueryFilter(e => e.TenantId == _currentTenantService.TenantId);
        modelBuilder.Entity<WebhookEvent>().HasQueryFilter(e => e.TenantId == _currentTenantService.TenantId);

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
