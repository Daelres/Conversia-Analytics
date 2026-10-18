using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Conversia.Application.DTOs.Auth;
using Conversia.Application.Interfaces;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Conversia.IntegrationTests;

/// <summary>The demo data the requirement promises, spelled out once so every demo-data suite asserts the same facts.</summary>
internal static class DemoData
{
    public const string DefaultPassword = "Demo-Pass-2026!";

    public const string AcmeTenantName = "Acme Demo";
    public const string GlobexTenantName = "Globex Demo";

    public const string AcmeOwnerEmail = "owner@acme-demo.example";
    public const string AcmeManagerEmail = "manager@acme-demo.example";
    public const string AcmeViewerEmail = "viewer@acme-demo.example";
    public const string AcmeNoAccessEmail = "noaccess@acme-demo.example";
    public const string GlobexOwnerEmail = "owner@globex-demo.example";

    /// <summary>The SKU both demo tenants use, to show that SKUs are unique per tenant and not globally.</summary>
    public const string SharedSku = "SKU-001";

    /// <summary>The 12 distinct Acme SKUs plus <see cref="SharedSku"/>.</summary>
    public const int AcmeProductCount = 13;

    /// <summary>Exactly the SKUs of "Acme Demo": <c>ACME-001</c>..<c>ACME-012</c> and the shared one.</summary>
    public static readonly string[] AcmeSkus =
        Enumerable.Range(1, 12).Select(i => $"ACME-{i:000}").Append(SharedSku).ToArray();

    public const int GlobexProductCount = 5;

    public const int AcmeUserCount = 4;

    public static readonly string[] AcmeEmails = { AcmeOwnerEmail, AcmeManagerEmail, AcmeViewerEmail, AcmeNoAccessEmail };

    public static readonly string[] AllEmails = AcmeEmails.Append(GlobexOwnerEmail).ToArray();

    public static readonly Permission[] ManagerPermissions =
        { Permission.ProductsView, Permission.ProductsManage, Permission.UsersView };

    public static readonly Permission[] ViewerPermissions = { Permission.ProductsView };
}

/// <summary>
/// A throw-away database on the shared PostgreSQL container. The demo-data suites seed fixed, well-known rows
/// (tenant names, emails, SKUs) that would collide with — or leak into — every other suite if they used the shared
/// database, so each test class owns a database of its own (migrated once, wiped between tests) and drops it when done.
/// </summary>
internal sealed class DemoDatabase : IAsyncDisposable
{
    private readonly string _masterConnectionString;

    private DemoDatabase(string masterConnectionString)
    {
        _masterConnectionString = masterConnectionString;
        Name = $"demo_{Guid.NewGuid():N}";
        ConnectionString = new NpgsqlConnectionStringBuilder(masterConnectionString) { Database = Name }.ConnectionString;
    }

    public string Name { get; }

    public string ConnectionString { get; }

    public static DemoDatabase Create(CustomWebApplicationFactory shared) => new(shared.ServerConnectionString);

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
        await db.Products.IgnoreQueryFilters().ExecuteDeleteAsync();
        await db.Users.IgnoreQueryFilters().ExecuteDeleteAsync();
        await db.Tenants.ExecuteDeleteAsync();
    }

    /// <summary>Deletes a tenant with everything that belongs to it (permissions, products, users).</summary>
    public async Task RemoveTenantAsync(Guid tenantId)
    {
        await using var db = NewContext();
        await db.UserPermissions.IgnoreQueryFilters()
            .Where(p => db.Users.IgnoreQueryFilters().Any(u => u.Id == p.UserId && u.TenantId == tenantId))
            .ExecuteDeleteAsync();
        await db.Products.IgnoreQueryFilters().Where(p => p.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Users.IgnoreQueryFilters().Where(u => u.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Tenants.Where(t => t.Id == tenantId).ExecuteDeleteAsync();
    }

    public ApplicationDbContext NewContext()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(ConnectionString).Options, new NoTenant());

    public async ValueTask DisposeAsync()
    {
        // Pooled connections to this database would otherwise outlive it (one pool per dropped database).
        using (var demoConnection = new NpgsqlConnection(ConnectionString))
        {
            NpgsqlConnection.ClearPool(demoConnection);
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

/// <summary>What the host under test is configured with. <c>null</c> means "the setting is not supplied at all".</summary>
internal sealed record DemoHostSettings(
    string? Enabled = null,
    string? Password = null,
    string Environment = "Development",
    string? ConnectionString = null)
{
    /// <summary>A database nothing listens on: connecting is refused at once, and a short timeout bounds everything else.</summary>
    public const string UnreachableConnectionString =
        "Host=127.0.0.1;Port=1;Database=Unreachable;Username=postgres;Password=change_me;Timeout=2;Command Timeout=2;Pooling=false";
}

/// <summary>
/// Everything a host logged: level, category, the formatted message and exception text (<see cref="Entry.Text"/>), and the
/// structured properties and scopes that came with it (<see cref="Entry.Properties"/>, <see cref="Entry.FullText"/>).
/// <para><see cref="Entry.Text"/> is only what the logger rendered (message plus exception); a value that was logged as a structured
/// property, such as the <c>ErrorId</c> of the registry, is not in it unless the message template prints it. <see cref="Entry.FullText"/>
/// adds every property and scope value, so use it (or <see cref="Entry.HasPropertyValue"/>) to ask "was this value logged at all?".</para>
/// </summary>
internal sealed class LogCapture : ILoggerProvider, ISupportExternalScope
{
    private readonly ConcurrentQueue<Entry> _entries = new();
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

    internal sealed record Entry(
        LogLevel Level, string Category, string Text, string Message, string Exception, IReadOnlyList<KeyValuePair<string, string>> Properties)
    {
        /// <summary>Message, exception and every property/scope value in one string.</summary>
        public string FullText => $"{Message} {Exception} {string.Join(' ', Properties.Select(p => $"{p.Key}={p.Value}"))}";

        public bool Mentions(string value) => FullText.Contains(value, StringComparison.Ordinal);

        public bool HasPropertyValue(string value) => Properties.Any(p => p.Value == value);
    }

    public IReadOnlyList<Entry> Entries => _entries.ToArray();

    public IEnumerable<Entry> Mentioning(string value) => Entries.Where(e => e.Mentions(value));

    /// <summary>Waits (bounded) until an entry matching <paramref name="predicate"/> exists, then returns it; fails the test otherwise.</summary>
    public async Task<Entry> WaitForAsync(Func<Entry, bool> predicate, string description, int timeoutSeconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (true)
        {
            var found = Entries.FirstOrDefault(predicate);
            if (found is not null)
            {
                return found;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new Xunit.Sdk.XunitException($"No log entry ({description}) within {timeoutSeconds}s. Warnings and errors seen: {string.Join(" | ", Entries.Where(e => e.Level >= LogLevel.Warning).Select(e => Truncate(e.FullText)).Take(10))}");
            }

            await Task.Delay(50);
        }
    }

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries, () => _scopes);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public void Dispose()
    {
    }

    private static string Truncate(string text) => text.Length <= 300 ? text : text[..300] + "...";

    private sealed class CapturingLogger(string category, ConcurrentQueue<Entry> sink, Func<IExternalScopeProvider> scopes) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => scopes().Push(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var properties = new List<KeyValuePair<string, string>>();
            AddProperties(state, properties);
            scopes().ForEachScope((scope, list) => AddProperties(scope, list), properties);
            var message = formatter(state, exception);
            sink.Enqueue(new Entry(logLevel, category, $"{message} {exception}", message, exception?.ToString() ?? string.Empty, properties));
        }

        private static void AddProperties(object? state, List<KeyValuePair<string, string>> into)
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                foreach (var pair in pairs)
                {
                    if (pair.Key != "{OriginalFormat}")
                    {
                        into.Add(new KeyValuePair<string, string>(pair.Key, Convert.ToString(pair.Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty));
                    }
                }
            }
        }
    }
}

/// <summary>
/// The real API booted in-process with the demo-data settings under test, on whichever database it is pointed at.
/// Booting happens on first use (<see cref="Start"/>, <see cref="CreateClient"/> or <see cref="Services"/>), which is
/// also when the API's startup seeding — if any — runs.
/// </summary>
internal sealed class DemoHost : WebApplicationFactory<Program>
{
    private readonly DemoHostSettings _settings;

    public DemoHost(DemoHostSettings settings)
    {
        _settings = settings;
    }

    /// <summary>
    /// A host that is built and abandoned (a boot the API refuses, or a test that never disposes it in time) keeps
    /// one inotify instance per watched config file until the GC finalizes it, and Linux allows only 128 per user.
    /// Hosts of these suites never need to reload configuration, so they do not watch the files at all.
    /// </summary>
    private static void NoFileWatchers(IWebHostBuilder builder)
        => builder.UseSetting("hostBuilder:reloadConfigOnChange", "false");

    public LogCapture Logs { get; } = new();

    public DbCommandCounter DbCommands { get; } = new();

    /// <summary>Boots the host (running its startup work) and returns it.</summary>
    public DemoHost Start()
    {
        _ = Services;
        return this;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_settings.Environment);
        NoFileWatchers(builder);
        builder.UseSetting("Jwt:SigningKey", TestJwtSettings.SigningKey);
        builder.UseSetting("Webhooks:Processing:Enabled", "false"); // no queue worker: these hosts assert on database traffic and logs
        builder.UseSetting("Webhooks:Retention:Enabled", "false");
        builder.UseSetting("Conversations:AutoCloseEnabled", "false");
        builder.UseSetting("RateLimiting:Login:PermitLimit", CustomWebApplicationFactory.UnthrottledPermitLimit);
        builder.UseSetting("RateLimiting:Login:WindowSeconds", "60");

        var connectionString = _settings.ConnectionString ?? DemoHostSettings.UnreachableConnectionString;
        builder.UseSetting("ConnectionStrings:DefaultConnection", connectionString);

        if (_settings.Enabled is not null)
        {
            builder.UseSetting("Demo:Enabled", _settings.Enabled);
        }

        if (_settings.Password is not null)
        {
            builder.UseSetting("Demo:Password", _settings.Password);
        }

        builder.ConfigureLogging(logging =>
        {
            logging.Services.Configure<LoggerFilterOptions>(options => options.Rules.Clear());
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddProvider(Logs);
        });

        builder.ConfigureServices(services =>
        {
            var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<ApplicationDbContext>));
            if (descriptor is not null)
            {
                services.Remove(descriptor);
            }

            services.AddDbContext<ApplicationDbContext>(options =>
                options.UseNpgsql(connectionString).AddInterceptors(DbCommands));
        });
    }

    /// <summary>Runs the registered seeder once, the way an explicit call from code would.</summary>
    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        using var scope = Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDemoDataSeeder>().SeedAsync(cancellationToken);
    }

    /// <summary>Runs <paramref name="read"/> against the host's own database, across all tenants (no query filters are applied by the caller's choice).</summary>
    public async Task<T> QueryAsync<T>(Func<ApplicationDbContext, Task<T>> read)
    {
        using var scope = Services.CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>());
    }

    public async Task<HttpResponseMessage> LoginRawAsync(string email, string password = DemoData.DefaultPassword)
    {
        using var client = CreateClient();
        return await client.PostAsJsonAsync("/api/auth/login", new { email, password });
    }

    /// <summary>Signs in through the real endpoint and returns a client carrying the bearer token plus the issued response.</summary>
    public async Task<DemoSession> SignInAsync(string email, string password = DemoData.DefaultPassword)
    {
        using var response = await LoginRawAsync(email, password);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var auth = (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);
        return new DemoSession(email, auth, client);
    }
}

internal sealed record DemoSession(string Email, AuthResponse Auth, HttpClient Client) : IDisposable
{
    public void Dispose() => Client.Dispose();
}

/// <summary>Reads and writes the demo database directly (across tenants), for assertions and for planting conflicting rows.</summary>
internal static class DemoDbReader
{
    public static Task<List<Tenant>> TenantsAsync(DemoDatabase db, string? name = null)
        => WithContext(db, c => c.Tenants.AsNoTracking().Where(t => name == null || t.Name == name).ToListAsync());

    public static Task<List<User>> UsersAsync(DemoDatabase db)
        => WithContext(db, c => c.Users.IgnoreQueryFilters().AsNoTracking().ToListAsync());

    public static Task<List<Product>> ProductsAsync(DemoDatabase db, Guid? tenantId = null)
        => WithContext(db, c => c.Products.IgnoreQueryFilters().AsNoTracking()
            .Where(p => tenantId == null || p.TenantId == tenantId).ToListAsync());

    public static Task<List<UserPermission>> PermissionsAsync(DemoDatabase db)
        => WithContext(db, c => c.UserPermissions.IgnoreQueryFilters().AsNoTracking().ToListAsync());

    public static async Task<Guid> TenantIdAsync(DemoDatabase db, string name)
        => Assert.Single(await TenantsAsync(db, name)).Id;

    public static async Task<User> UserAsync(DemoDatabase db, string email)
        => Assert.Single(await UsersAsync(db), u => u.Email == email);

    public static async Task<List<Permission>> PermissionsOfAsync(DemoDatabase db, string email)
    {
        var user = await UserAsync(db, email);
        return (await PermissionsAsync(db)).Where(p => p.UserId == user.Id).Select(p => p.Permission).Order().ToList();
    }

    /// <summary>
    /// Every persisted row, rendered to text (ids, hashes, row versions and audit stamps included), so two snapshots
    /// are equal only if nothing was added, removed or changed in between.
    /// </summary>
    public static async Task<IReadOnlyList<string>> SnapshotAsync(DemoDatabase db)
    {
        var tenants = await TenantsAsync(db);
        var users = await UsersAsync(db);
        var permissions = await PermissionsAsync(db);
        var products = await ProductsAsync(db);

        return tenants.Select(t => $"T|{t.Id}|{t.Name}|{t.CreatedAt:O}|{t.UpdatedAt:O}")
            .Concat(users.Select(u => $"U|{u.Id}|{u.TenantId}|{u.Email}|{u.PasswordHash}|{u.IsOwner}|{u.CreatedAt:O}|{u.UpdatedAt:O}"))
            .Concat(permissions.Select(p => $"P|{p.Id}|{p.UserId}|{p.Permission}"))
            .Concat(products.Select(p =>
                $"R|{p.Id}|{p.TenantId}|{p.Sku}|{p.Name}|{p.Price}|{p.Stock}|{p.RowVersion}|{p.CreatedAt:O}|{p.UpdatedAt:O}"))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>A tenant with one user (and no products) holding <paramref name="email"/> — somebody already using a demo address.</summary>
    public static async Task<(Guid TenantId, Guid UserId)> PlantSquatterAsync(DemoDatabase db, string email, bool isOwner = false)
    {
        var hash = new Microsoft.AspNetCore.Identity.PasswordHasher<User>().HashPassword(null!, TestHelpers.StrongPassword);
        await using var context = db.NewContext();
        var tenant = Tenant.Create($"Squatter {Guid.NewGuid():N}");
        context.Tenants.Add(tenant);
        var user = User.Create(tenant.Id, email, hash, isOwner);
        context.Users.Add(user);
        await context.SaveChangesAsync();
        return (tenant.Id, user.Id);
    }

    /// <summary>Removes what <see cref="PlantSquatterAsync"/> planted, as if the conflicting account had never existed.</summary>
    public static async Task RemoveSquatterAsync(DemoDatabase db, (Guid TenantId, Guid UserId) squatter)
    {
        await using var context = db.NewContext();
        await context.Users.IgnoreQueryFilters().Where(u => u.Id == squatter.UserId).ExecuteDeleteAsync();
        await context.Tenants.Where(t => t.Id == squatter.TenantId).ExecuteDeleteAsync();
    }

    private static async Task<T> WithContext<T>(DemoDatabase db, Func<ApplicationDbContext, Task<T>> read)
    {
        await using var context = db.NewContext();
        return await read(context);
    }
}

/// <summary>Walks an exception chain (including aggregates) looking for a typed match, as the startup suites do.</summary>
internal static class ExceptionChain
{
    public static T? Find<T>(Exception root, Func<T, bool> predicate) where T : Exception
    {
        for (var e = root; e is not null; e = e.InnerException)
        {
            if (e is T match && predicate(match))
            {
                return match;
            }

            if (e is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    var found = Find(inner, predicate);
                    if (found is not null)
                    {
                        return found;
                    }
                }
            }
        }

        return null;
    }

    public static string Describe(Exception root)
    {
        var parts = new List<string>();
        for (var e = root; e is not null; e = e.InnerException)
        {
            parts.Add($"{e.GetType().Name}: {e.Message}");
        }

        return string.Join(" -> ", parts);
    }

    public static IEnumerable<Exception> Flatten(Exception root)
    {
        for (var e = root; e is not null; e = e.InnerException)
        {
            yield return e;
            if (e is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions.SelectMany(Flatten))
                {
                    yield return inner;
                }
            }
        }
    }
}

/// <summary>
/// What one demo-data test class shares across its tests (xUnit class fixture): a single database that is created and
/// migrated on first use — the migration is by far the most expensive step — plus any host or sign-in that is
/// expensive to build and read-only to the tests. Everything is torn down when the class is done.
/// </summary>
public sealed class DemoClassState : IAsyncLifetime
{
    private readonly object _gate = new();
    private readonly List<object> _shared = new();
    private readonly Dictionary<string, Lazy<Task<object>>> _byKey = new();
    private DemoDatabase? _database;
    private Lazy<Task>? _migration;

    internal bool IsMigrated => _migration is { IsValueCreated: true } && _migration.Value.IsCompletedSuccessfully;

    internal DemoDatabase Database(CustomWebApplicationFactory shared)
    {
        lock (_gate)
        {
            if (_database is null)
            {
                _database = DemoDatabase.Create(shared);
                var database = _database;
                _migration = new Lazy<Task>(database.MigrateAsync);
            }

            return _database;
        }
    }

    /// <summary>Migrates the class database the first time anyone asks and does nothing afterwards.</summary>
    internal Task EnsureMigratedAsync() => _migration!.Value;

    /// <summary>Builds <paramref name="create"/> once per class; the value is disposed with the class, newest first.</summary>
    internal async Task<T> SharedAsync<T>(string key, Func<Task<T>> create) where T : notnull
    {
        Lazy<Task<object>> entry;
        lock (_gate)
        {
            if (!_byKey.TryGetValue(key, out entry!))
            {
                entry = new Lazy<Task<object>>(async () =>
                {
                    var value = await create();
                    lock (_gate)
                    {
                        _shared.Add(value);
                    }

                    return value;
                });
                _byKey[key] = entry;
            }
        }

        return (T)await entry.Value;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        try
        {
            foreach (var value in Enumerable.Reverse(_shared))
            {
                switch (value)
                {
                    case IAsyncDisposable asyncDisposable:
                        await asyncDisposable.DisposeAsync();
                        break;
                    case IDisposable disposable:
                        disposable.Dispose();
                        break;
                }
            }
        }
        finally
        {
            if (_database is not null)
            {
                await _database.DisposeAsync();
            }
        }
    }
}

/// <summary>
/// Base of the database-backed demo-data suites. Each class gets one database (migrated once, see
/// <see cref="DemoClassState"/>) that is emptied before every test; hosts built per test are disposed with it, and a
/// test that needs a database in another state (not there, not migrated) asks for a private one.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public abstract class DemoDataTestBase : IAsyncLifetime, IClassFixture<DemoClassState>
{
    private readonly List<DemoHost> _hosts = new();
    private readonly List<DemoDatabase> _privateDatabases = new();
    private readonly CustomWebApplicationFactory _container;
    private readonly DemoClassState _state;

    protected DemoDataTestBase(CustomWebApplicationFactory container, DemoClassState state)
    {
        _container = container;
        _state = state;
        Db = state.Database(container);
    }

    /// <summary>The class database. Empty at the start of every test unless the class opts out of the reset.</summary>
    internal DemoDatabase Db { get; }

    /// <summary>False for classes whose tests only read what the class seeded once.</summary>
    protected virtual bool ResetsDatabaseBetweenTests => true;

    public async Task InitializeAsync()
    {
        if (ResetsDatabaseBetweenTests && _state.IsMigrated)
        {
            await Db.WipeAsync();
        }
    }

    public virtual async Task DisposeAsync()
    {
        try
        {
            foreach (var host in _hosts)
            {
                host.Dispose();
            }
        }
        finally
        {
            foreach (var database in _privateDatabases)
            {
                await database.DisposeAsync();
            }
        }
    }

    /// <summary>Creates and migrates the class database unless an earlier test of the class already did.</summary>
    internal Task MigrateAsync() => _state.EnsureMigratedAsync();

    /// <summary>A database of this test alone (not created yet), dropped when the test ends.</summary>
    internal DemoDatabase NewPrivateDatabase()
    {
        var database = DemoDatabase.Create(_container);
        _privateDatabases.Add(database);
        return database;
    }

    /// <summary>A host on the class database (unless the settings name another). It is not booted until first used.</summary>
    internal DemoHost NewHost(DemoHostSettings settings, DemoDatabase? database = null)
    {
        var host = new DemoHost(settings with { ConnectionString = settings.ConnectionString ?? (database ?? Db).ConnectionString });
        lock (_hosts)
        {
            _hosts.Add(host);
        }

        return host;
    }

    /// <summary>A migrated, empty class database and a new API started on it with the demo data enabled (so its startup seeding has run).</summary>
    internal async Task<DemoHost> StartSeededHostAsync(string environment = "Development", string? password = null)
    {
        await MigrateAsync();
        return NewHost(new DemoHostSettings(Enabled: "true", Password: password, Environment: environment)).Start();
    }

    /// <summary>
    /// The class's one API, started with the demo data enabled on a database that was not migrated yet (its startup
    /// seeding fails and is logged, the API stays up), then returned with a migrated database holding no rows: the
    /// state of a database that has just been migrated, for tests that call the seeder explicitly.
    /// </summary>
    internal async Task<DemoHost> StartEmptyHostAsync()
    {
        var host = await _state.SharedAsync("empty-host", async () =>
        {
            var started = new DemoHost(new DemoHostSettings(Enabled: "true", ConnectionString: Db.ConnectionString)).Start();
            await MigrateAsync();
            return started;
        });
        await Db.WipeAsync();
        return host;
    }

    /// <summary>The class's one API with the demo data enabled, started (and so startup-seeded) on the migrated class database.</summary>
    internal Task<DemoHost> StartSharedSeededHostAsync() => _state.SharedAsync("seeded-host", async () =>
    {
        await MigrateAsync();
        return new DemoHost(new DemoHostSettings(Enabled: "true", ConnectionString: Db.ConnectionString)).Start();
    });

    /// <summary>Builds <paramref name="create"/> once for the whole class (see <see cref="DemoClassState.SharedAsync{T}"/>).</summary>
    internal Task<T> SharedAsync<T>(string key, Func<Task<T>> create) where T : notnull => _state.SharedAsync(key, create);
}

/// <summary>The API with the demo data seeded and every demo account signed in, for the demo-data API suites.</summary>
internal sealed record DemoWorld(
    DemoHost Host, DemoSession Owner, DemoSession Manager, DemoSession Viewer, DemoSession NoAccess, DemoSession Globex) : IDisposable
{
    public static async Task<DemoWorld> SignInEveryoneAsync(DemoHost host) => new(
        host,
        await host.SignInAsync(DemoData.AcmeOwnerEmail),
        await host.SignInAsync(DemoData.AcmeManagerEmail),
        await host.SignInAsync(DemoData.AcmeViewerEmail),
        await host.SignInAsync(DemoData.AcmeNoAccessEmail),
        await host.SignInAsync(DemoData.GlobexOwnerEmail));

    public void Dispose()
    {
        foreach (var session in new[] { Owner, Manager, Viewer, NoAccess, Globex })
        {
            session.Dispose();
        }
    }
}

/// <summary>Helpers for the demo-data API suites.</summary>
internal static class DemoApi
{
    /// <summary>Every product the caller's tenant has, in one page.</summary>
    public static async Task<List<Conversia.Application.DTOs.Products.ProductResponse>> AllProductsAsync(HttpClient client)
    {
        var page = await TestHelpers.ListProductsOkAsync(client, "pageSize=100");
        Assert.Equal(page.TotalCount, page.Items.Count);
        return page.Items.ToList();
    }
}
