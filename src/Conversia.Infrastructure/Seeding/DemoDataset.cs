using Conversia.Application.Demo;
using Conversia.Domain.Enums;

namespace Conversia.Infrastructure.Seeding;

internal sealed record DemoUserSeed(string Email, bool IsOwner, IReadOnlyList<Permission> Permissions);

internal sealed record DemoProductSeed(string Sku, string Name, decimal Price, int Stock);

internal sealed record DemoTenantSeed(string Name, IReadOnlyList<DemoUserSeed> Users, IReadOnlyList<DemoProductSeed> Products)
{
    /// <summary>Identifies the tenant: it counts as seeded as soon as this email exists, whoever owns it now.</summary>
    public string OwnerEmail => Users.Single(u => u.IsOwner).Email;
}

internal static class DemoDataset
{
    public const string SharedSku = "SKU-001";

    public static IReadOnlyList<DemoTenantSeed> Tenants { get; } = [Acme(), Globex()];

    private static DemoTenantSeed Acme() => new(
        "Acme Demo",
        [Seed(DemoAccounts.AcmeOwner), Seed(DemoAccounts.AcmeManager), Seed(DemoAccounts.AcmeViewer), Seed(DemoAccounts.AcmeNoAccess)],
        [
            new("ACME-001", "Yunque de 50 kg", 249.90m, 14),
            new("ACME-002", "Patines cohete", 1299.00m, 3),
            new("ACME-003", "Liga de goma gigante", 12.50m, 240),
            new("ACME-004", "Agujero portátil (paquete de 3)", 89.99m, 32),
            new("ACME-005", "Rocas deshidratadas", 7.25m, 500),
            new("ACME-006", "Pastillas para terremotos", 45.00m, 0),
            new("ACME-007", "Imán gigante", 399.95m, 8),
            new("ACME-008", "Bastón saltarín a reacción", 179.00m, 21),
            new("ACME-009", "Carretera instantánea", 59.90m, 67),
            new("ACME-010", "Alpiste de hierro para pájaros", 4.99m, 1200),
            new("ACME-011", "Semillas de tornado", 24.75m, 0),
            new("ACME-012", "Botas con resortes", 119.00m, 15),
            new(SharedSku, "Caja de envío estándar", 35.00m, 90),
        ]);

    private static DemoTenantSeed Globex() => new(
        "Globex Demo",
        [Seed(DemoAccounts.GlobexOwner)],
        [
            new(SharedSku, "Carcasa de la máquina del juicio final", 15000.00m, 2),
            new("GLX-002", "Talonario de boletos de monorriel", 19.50m, 300),
            new("GLX-003", "Lámpara de escritorio hipnótica", 64.25m, 40),
            new("GLX-004", "Silla giratoria ejecutiva", 429.00m, 0),
            new("GLX-005", "Suscripción mensual de rosquillas", 9.99m, 1000),
        ]);

    private static DemoUserSeed Seed(DemoAccount account) => new(account.Email, account.IsOwner, account.Permissions);
}
