using Conversia.Application.Interfaces;
using Conversia.Domain.Entities;
using Conversia.Infrastructure.Configuration;
using Conversia.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Conversia.Infrastructure.Seeding;

public sealed class DemoDataSeeder(
    ApplicationDbContext dbContext,
    IPasswordHasherService passwordHasher,
    DemoDataOptions options,
    ILogger<DemoDataSeeder> logger) : IDemoDataSeeder
{
    // Key of the advisory lock that serializes the seeders (an arbitrary constant, ASCII "ConvDemo").
    private const long SeedingLockKey = 0x436F6E7644656D6F;
    // Well below Npgsql's 30 s command timeout, so the lock wait fails with a PostgreSQL error before the client gives up.
    private const string SeedingLockTimeoutSql = "SET LOCAL lock_timeout = '15s'";
    private const string SeedingLockTimeoutResetSql = "SET LOCAL lock_timeout = 0";

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        foreach (var tenant in DemoDataset.Tenants)
        {
            await SeedTenantAsync(tenant, cancellationToken);
        }
    }

    private async Task SeedTenantAsync(DemoTenantSeed seed, CancellationToken cancellationToken)
    {
        // Cheap first check without lock or transaction: restarts and second instances cost one statement per tenant.
        if (await OwnerExistsAsync(seed, cancellationToken))
        {
            logger.LogInformation("Demo tenant '{Tenant}' already exists, skipping.", seed.Name);
            return;
        }

        // The manual transaction requires that no retrying execution strategy (EnableRetryOnFailure) is configured.
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await AcquireSeedingLockAsync(cancellationToken);

        // Second check under the lock: another seeder may have finished while this one waited.
        if (await OwnerExistsAsync(seed, cancellationToken))
        {
            logger.LogInformation("Demo tenant '{Tenant}' already exists, skipping.", seed.Name);
            return;
        }

        var added = Stage(seed);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            // Backstop for what the lock cannot see (a user registering one of the demo emails meanwhile):
            // the unique email index arbitrates, the transaction rolls back, nothing is left half-seeded.
            dbContext.ChangeTracker.Clear();
            logger.LogInformation("One of the emails of demo tenant '{Tenant}' is already taken, skipping it.", seed.Name);
            return;
        }

        logger.LogInformation(
            "Seeded demo tenant '{Tenant}': {UserCount} users, {PermissionCount} permissions, {ProductCount} products ({Emails}).",
            seed.Name, added.Users, added.Permissions, added.Products, string.Join(", ", seed.Users.Select(u => u.Email)));
    }

    // No tenant is resolved outside an HTTP request, so the user query filter would hide every row.
    private Task<bool> OwnerExistsAsync(DemoTenantSeed seed, CancellationToken cancellationToken) =>
        dbContext.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == seed.OwnerEmail, cancellationToken);

    // Serializing seeders (also across API instances) turns two racing inserts of the same unique emails into a plain
    // "already seeded" check. The transaction-level advisory lock is released when the transaction ends.
    private async Task AcquireSeedingLockAsync(CancellationToken cancellationToken)
    {
        // SET LOCAL takes no parameters; the values are constants. The timeout bounds the wait, which pg_advisory_xact_lock has no
        // argument for, and is lifted once the lock is held so it does not apply to the inserts that follow in this transaction.
        await dbContext.Database.ExecuteSqlRawAsync(SeedingLockTimeoutSql, cancellationToken);
        await dbContext.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({SeedingLockKey})", cancellationToken);
        await dbContext.Database.ExecuteSqlRawAsync(SeedingLockTimeoutResetSql, cancellationToken);
    }

    private (int Users, int Permissions, int Products) Stage(DemoTenantSeed seed)
    {
        var tenant = Tenant.Create(seed.Name);
        var users = seed.Users
            .Select(u => (Seed: u, Entity: User.Create(tenant.Id, u.Email, passwordHasher.HashPassword(options.Password), u.IsOwner)))
            .ToList();
        var permissions = users
            .SelectMany(u => u.Seed.Permissions.Select(p => UserPermission.Create(u.Entity.Id, p)))
            .ToList();
        var products = seed.Products
            .Select(p => Product.Create(tenant.Id, p.Name, p.Sku, p.Price, p.Stock))
            .ToList();

        dbContext.Tenants.Add(tenant);
        dbContext.Users.AddRange(users.Select(u => u.Entity));
        dbContext.UserPermissions.AddRange(permissions);
        dbContext.Products.AddRange(products);

        return (users.Count, permissions.Count, products.Count);
    }
}
