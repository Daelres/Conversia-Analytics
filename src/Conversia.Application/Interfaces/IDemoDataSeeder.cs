namespace Conversia.Application.Interfaces;

/// <summary>
/// Creates the opt-in demo tenants (users, permissions, products). Idempotent and
/// safe to call concurrently: a tenant is seeded only if its owner email does not exist yet.
/// </summary>
public interface IDemoDataSeeder
{
    Task SeedAsync(CancellationToken cancellationToken = default);
}
