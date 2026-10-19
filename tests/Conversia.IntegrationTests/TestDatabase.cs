using Conversia.Application.Interfaces;
using Conversia.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Conversia.IntegrationTests;

/// <summary>
/// A throw-away database on the shared PostgreSQL container. Suites that need an empty or not yet migrated database, or that
/// would collide with — or leak into — every other suite if they used the shared one, own a database of their own
/// (migrated once, wiped between tests) and drop it when done.
/// </summary>
internal sealed class TestDatabase : IAsyncDisposable
{
    private readonly string _masterConnectionString;

    private TestDatabase(string masterConnectionString)
    {
        _masterConnectionString = masterConnectionString;
        Name = $"test_{Guid.NewGuid():N}";
        ConnectionString = new NpgsqlConnectionStringBuilder(masterConnectionString) { Database = Name }.ConnectionString;
    }

    public string Name { get; }

    public string ConnectionString { get; }

    /// <summary>A database nothing listens on: connecting is refused at once, and a short timeout bounds everything else.</summary>
    public const string UnreachableConnectionString =
        "Host=127.0.0.1;Port=1;Database=Unreachable;Username=postgres;Password=change_me;Timeout=2;Command Timeout=2;Pooling=false";

    public static TestDatabase Create(CustomWebApplicationFactory shared) => new(shared.ServerConnectionString);

    /// <summary>Creates the database and applies every migration.</summary>
    public async Task MigrateAsync()
    {
        await using var db = NewContext();
        await db.Database.MigrateAsync();
    }

    /// <summary>Creates a database with no tables at all — "exists but not migrated yet".</summary>
    public async Task CreateEmptyAsync() => await ExecuteOnMasterAsync($"CREATE DATABASE \"{Name}\"");

    /// <summary>Deletes every row, as a fresh database would have none, without paying for a new migration.</summary>
    public async Task WipeAsync()
    {
        await using var db = NewContext();
        await db.UserPermissions.IgnoreQueryFilters().ExecuteDeleteAsync();
        await db.Users.IgnoreQueryFilters().ExecuteDeleteAsync();
        await db.Tenants.ExecuteDeleteAsync();
    }

    /// <summary>Deletes a tenant with everything that belongs to it (permissions, users).</summary>
    public async Task RemoveTenantAsync(Guid tenantId)
    {
        await using var db = NewContext();
        await db.UserPermissions.IgnoreQueryFilters()
            .Where(p => db.Users.IgnoreQueryFilters().Any(u => u.Id == p.UserId && u.TenantId == tenantId))
            .ExecuteDeleteAsync();
        await db.Users.IgnoreQueryFilters().Where(u => u.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Tenants.Where(t => t.Id == tenantId).ExecuteDeleteAsync();
    }

    public ApplicationDbContext NewContext()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(ConnectionString).Options, new NoTenant());

    public async ValueTask DisposeAsync()
    {
        // Pooled connections to this database would otherwise outlive it (one pool per dropped database).
        using (var databaseConnection = new NpgsqlConnection(ConnectionString))
        {
            NpgsqlConnection.ClearPool(databaseConnection);
        }

        // FORCE disconnects whoever is still connected (a host that was not disposed in time) instead of failing.
        await ExecuteOnMasterAsync($"DROP DATABASE IF EXISTS \"{Name}\" WITH (FORCE)");
    }

    private async Task ExecuteOnMasterAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_masterConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class NoTenant : ICurrentTenantService
    {
        public Guid TenantId => Guid.Empty;
    }
}
