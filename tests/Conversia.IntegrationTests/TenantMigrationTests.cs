using Conversia.Domain.Enums;
using Conversia.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Conversia.IntegrationTests;

/// <summary>
/// The upgrade of a database that already holds companies: the migration that introduces the tenant status must leave them operating
/// (Active, quota max(4, active lines)) and must not touch their channels.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class TenantMigrationTests
{
    private const string PreviousMigration = "20261019165831_RemoveProducts";

    private readonly CustomWebApplicationFactory _factory;

    public TenantMigrationTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task ExistingTenants_BecomeActive_WithAQuotaOfAtLeastFourAndNeverBelowTheirActiveLines_AndChannelsAreUntouched()
    {
        await using var database = TestDatabase.Create(_factory);
        await database.CreateEmptyAsync();
        await using (var old = database.NewContext())
        {
            await old.GetService<IMigrator>().MigrateAsync(PreviousMigration);
        }

        var quiet = Guid.NewGuid();
        var small = Guid.NewGuid();
        var busy = Guid.NewGuid();
        var quietBranch = Guid.NewGuid();
        var smallBranch = Guid.NewGuid();
        var busyBranch = Guid.NewGuid();
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            foreach (var (tenant, branch, name) in new[] { (quiet, quietBranch, "Quiet"), (small, smallBranch, "Small"), (busy, busyBranch, "Busy") })
            {
                await ExecuteAsync(connection, $"""INSERT INTO "Tenants" ("Id", "Name", "CreatedAt") VALUES ('{tenant}', '{name}', now())""");
                await ExecuteAsync(connection, $"""INSERT INTO "Branches" ("Id", "TenantId", "Name", "IsActive", "CreatedAt") VALUES ('{branch}', '{tenant}', 'Sede', true, now())""");
            }

            // quiet: no channels. small: two active and one inactive. busy: six active.
            await InsertChannelAsync(connection, small, smallBranch, "A", active: true);
            await InsertChannelAsync(connection, small, smallBranch, "B", active: true);
            await InsertChannelAsync(connection, small, smallBranch, "C", active: false);
            for (var i = 0; i < 6; i++)
            {
                await InsertChannelAsync(connection, busy, busyBranch, $"D{i}", active: true);
            }
        }

        await using (var upgraded = database.NewContext())
        {
            await upgraded.Database.MigrateAsync();
        }

        await using var db = database.NewContext();
        var tenants = await db.Tenants.AsNoTracking().ToDictionaryAsync(t => t.Id);
        Assert.All(tenants.Values, t => Assert.Equal(TenantStatus.Active, t.Status));
        Assert.Equal(4, tenants[quiet].MaxActiveLines);
        Assert.Equal(4, tenants[small].MaxActiveLines);
        Assert.Equal(6, tenants[busy].MaxActiveLines);
        Assert.All(tenants.Values, t => Assert.Null(t.StatusChangedBy));
        Assert.All(tenants.Values, t => Assert.NotNull(t.StatusChangedAt));

        var channels = await db.WhatsAppChannels.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        Assert.Equal(9, channels.Count);
        Assert.Equal(2, channels.Count(c => c.TenantId == small && c.IsActive));
        Assert.Equal(1, channels.Count(c => c.TenantId == small && !c.IsActive));
        Assert.Equal(6, channels.Count(c => c.TenantId == busy && c.IsActive));

        // A tenant created after the upgrade is Pending: the old rows' Active came from the migration, not from a column default.
        await using var fresh = database.NewContext();
        var pending = Conversia.Domain.Entities.Tenant.Create("Fresh");
        fresh.Add(pending);
        await fresh.SaveChangesAsync();
        Assert.Equal(TenantStatus.Pending, (await fresh.Tenants.AsNoTracking().SingleAsync(t => t.Id == pending.Id)).Status);
    }

    [Fact]
    public async Task TheQuotaIsBoundedByTheDatabaseToo()
    {
        await using var database = TestDatabase.Create(_factory);
        await database.MigrateAsync();
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();

        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection,
            $"""INSERT INTO "Tenants" ("Id", "Name", "CreatedAt", "Status", "MaxActiveLines") VALUES ('{Guid.NewGuid()}', 'Too many', now(), 'Active', 1001)"""));
        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
    }

    private static async Task InsertChannelAsync(NpgsqlConnection connection, Guid tenant, Guid branch, string number, bool active)
        => await ExecuteAsync(connection, $"""
            INSERT INTO "WhatsAppChannels" ("Id", "TenantId", "BranchId", "DisplayPhoneNumber", "ChannelKey", "WebhookSecretHash", "IsActive", "CreatedAt")
            VALUES ('{Guid.NewGuid()}', '{tenant}', '{branch}', '3001234{number.GetHashCode() & 0xFFF:D4}', '{Guid.NewGuid():N}{Guid.NewGuid():N}', '\x00', {(active ? "true" : "false")}, now())
            """);

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
