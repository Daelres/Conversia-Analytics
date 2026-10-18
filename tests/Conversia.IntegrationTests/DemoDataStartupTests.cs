using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Conversia.IntegrationTests;

/// <summary>
/// The API's startup seeding (<c>Demo:Enabled</c>): data is there when the host has started and before it serves,
/// nothing is touched when the setting is off, a second instance changes nothing, a custom password applies, the
/// password is never logged, and a database that is unreachable or not migrated yet never takes the API down.
/// </summary>
public class DemoDataStartupTests(CustomWebApplicationFactory container, DemoClassState state) : DemoDataTestBase(container, state)
{
    private const string CustomPassword = "Zq9-Unique-Demo-Pw!";

    // ---- Seeding on startup --------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Development", "true")]
    [InlineData("Demo", "TRUE")]
    public async Task Start_WithDemoEnabledAndAMigratedDatabase_HasTheCompleteDemoDataWhenTheHostHasStarted(string environment, string enabled)
    {
        await MigrateAsync();
        var host = NewHost(new DemoHostSettings(enabled, Environment: environment));

        host.Start();

        await DemoDatasetAssertions.AssertCompleteAsync(Db);
        await DemoDatasetAssertions.AssertPasswordsHashedAsync(host, Db, DemoData.DefaultPassword);
    }

    [Fact]
    public async Task Start_WithDemoEnabled_ServesTheFirstRequestWithTheDemoAccountsAlreadyUsable()
    {
        await MigrateAsync();
        var host = NewHost(new DemoHostSettings("true"));

        // No waiting between the start and the first call: seeding has finished before the app serves.
        using var response = await host.LoginRawAsync(DemoData.AcmeOwnerEmail);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("False")]
    public async Task Start_WithDemoDisabledOrAbsent_SeedsNothingAndSendsNoCommandToTheDatabase(string? enabled)
    {
        await MigrateAsync();
        var host = NewHost(new DemoHostSettings(enabled));

        host.Start();

        Assert.Equal(0, host.DbCommands.Count);
        Assert.Empty(await DemoDbReader.TenantsAsync(Db));
        Assert.Empty(await DemoDbReader.UsersAsync(Db));
        Assert.Empty(await DemoDbReader.ProductsAsync(Db));
    }

    [Fact]
    public async Task Start_WithDemoEnabled_DoesNotTouchExistingNonDemoTenants()
    {
        await MigrateAsync();
        var bystander = await DemoDbReader.PlantSquatterAsync(Db, TestHelpers.UniqueEmail("bystander"), isOwner: true);
        var before = await DemoDbReader.SnapshotAsync(Db);

        NewHost(new DemoHostSettings("true")).Start();

        var after = await DemoDbReader.SnapshotAsync(Db);
        Assert.All(before, row => Assert.Contains(row, after));
        Assert.Equal(3, (await DemoDbReader.TenantsAsync(Db)).Count);
        Assert.Equal(DemoData.AllEmails.Length + 1, (await DemoDbReader.UsersAsync(Db)).Count);
        Assert.Contains(await DemoDbReader.UsersAsync(Db), u => u.TenantId == bystander.TenantId);
    }

    // ---- The password ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Start_WithACustomPassword_EveryDemoAccountSignsInWithItAndNotWithTheDefault()
    {
        const string password = CustomPassword;
        var host = await StartSeededHostAsync(password: password);

        foreach (var email in DemoData.AllEmails)
        {
            using var accepted = await host.LoginRawAsync(email, password);
            using var refused = await host.LoginRawAsync(email, DemoData.DefaultPassword);
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        }

        await DemoDatasetAssertions.AssertPasswordsHashedAsync(host, Db, password);
    }

    [Fact]
    public async Task Start_WithTheLongestAllowedPassword_EveryDemoAccountSignsInWithIt()
    {
        var password = "Aa1" + new string('x', 125);
        Assert.Equal(128, password.Length);
        var host = await StartSeededHostAsync(password: password);

        foreach (var email in DemoData.AllEmails)
        {
            using var response = await host.LoginRawAsync(email, password);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData(CustomPassword)]
    public async Task Start_WithDemoEnabled_NeverWritesThePasswordToTheLogs(string? customPassword)
    {
        var password = customPassword ?? DemoData.DefaultPassword;
        var host = await StartSeededHostAsync(password: customPassword);
        using var login = await host.LoginRawAsync(DemoData.AcmeOwnerEmail, password);

        // Seeding really happened (so the host had every chance to log the password) ...
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await DemoDatasetAssertions.AssertCompleteAsync(Db);

        // ... and nothing it logged contains the password.
        Assert.NotEmpty(host.Logs.Entries);
        Assert.All(host.Logs.Entries, entry => Assert.DoesNotContain(password, entry.Text));
    }

    // ---- Restarts and second instances -------------------------------------------------------------------------------

    [Fact]
    public async Task Start_OfASecondInstanceOnTheSameDatabase_CreatesAndChangesNothing()
    {
        var first = await StartSeededHostAsync();
        var before = await DemoDbReader.SnapshotAsync(Db);

        NewHost(new DemoHostSettings("true")).Start();
        NewHost(new DemoHostSettings("true", Environment: "Demo")).Start();

        Assert.Equal(before, await DemoDbReader.SnapshotAsync(Db));
        using var login = await first.LoginRawAsync(DemoData.GlobexOwnerEmail);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task Start_AfterTheUserDeletedAndEditedDemoData_RestoresAndOverwritesNothing()
    {
        var first = await StartSeededHostAsync();
        using (var owner = await first.SignInAsync(DemoData.AcmeOwnerEmail))
        {
            var page = await TestHelpers.ListProductsOkAsync(owner.Client, "pageSize=100");
            var toDelete = page.Items.First(p => p.Sku != DemoData.SharedSku);
            var toEdit = page.Items.First(p => p.Sku == DemoData.SharedSku);
            Assert.Equal(HttpStatusCode.NoContent, (await TestHelpers.DeleteProductAsync(owner.Client, toDelete.Id)).StatusCode);
            await TestHelpers.UpdateProductOkAsync(owner.Client, toEdit, "Edited through the API", 1.5m, 99);
        }

        var before = await DemoDbReader.SnapshotAsync(Db);

        NewHost(new DemoHostSettings("true")).Start();

        Assert.Equal(before, await DemoDbReader.SnapshotAsync(Db));
        Assert.Equal(DemoData.AcmeProductCount - 1, (await DemoDbReader.ProductsAsync(Db, await DemoDbReader.TenantIdAsync(Db, DemoData.AcmeTenantName))).Count);
    }

    [Fact]
    public async Task Start_WhenAnotherTenantAlreadyUsesADemoEmail_StillStartsAndLeavesNothingHalfSeeded()
    {
        await MigrateAsync();
        var squatter = await DemoDbReader.PlantSquatterAsync(Db, DemoData.AcmeViewerEmail);
        var host = NewHost(new DemoHostSettings("true"));

        var failure = Record.Exception(() => host.Start());

        Assert.Null(failure);
        await DemoDatasetAssertions.AssertNoHalfSeededTenantAsync(Db);
        Assert.Empty(await DemoDbReader.TenantsAsync(Db, DemoData.AcmeTenantName));

        await DemoDbReader.RemoveSquatterAsync(Db, squatter);
        await host.SeedAsync();
        await DemoDatasetAssertions.AssertCompleteAsync(Db);
    }

    // ---- A seeding failure never takes the API down ---------------------------------------------------------------------

    [Theory]
    [InlineData("Development", null)]
    [InlineData("Demo", null)]
    [InlineData("Development", CustomPassword)]
    public async Task Start_WithDemoEnabledAndAnUnreachableDatabase_StillServesAndLogsTheErrorWithoutThePassword(string environment, string? customPassword)
    {
        var password = customPassword ?? DemoData.DefaultPassword;
        var host = NewHost(new DemoHostSettings(
            "true", customPassword, environment, DemoHostSettings.UnreachableConnectionString));

        var failure = Record.Exception(() => host.Start());

        Assert.True(failure is null, $"A seeding failure must not stop the API from starting: {failure}");
        Assert.Contains(host.Logs.Entries, e => e.Level >= LogLevel.Error);
        using var client = host.CreateClient();
        using var response = await client.PostAsync("/api/auth/login", new StringContent("{not json", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.All(host.Logs.Entries, entry => Assert.DoesNotContain(password, entry.Text));
    }

    [Fact]
    public async Task Start_WithDemoEnabledAndADatabaseThatDoesNotExistYet_StillServesLogsTheErrorAndSeedsOnceMigrated()
    {
        var database = NewPrivateDatabase();
        var host = NewHost(new DemoHostSettings("true"), database);

        var failure = Record.Exception(() => host.Start());

        Assert.True(failure is null, $"A database that is not there yet must not stop the API from starting: {failure}");
        Assert.Contains(host.Logs.Entries, e => e.Level >= LogLevel.Error);
        await AssertSeedsOnceMigratedAsync(host, database);
    }

    [Fact]
    public async Task Start_WithDemoEnabledAndAnEmptyUnmigratedDatabase_StillServesLogsTheErrorAndSeedsOnceMigrated()
    {
        var database = NewPrivateDatabase();
        await database.CreateEmptyAsync();
        var host = NewHost(new DemoHostSettings("true", Environment: "Demo"), database);

        var failure = Record.Exception(() => host.Start());

        Assert.True(failure is null, $"A database that is not migrated yet must not stop the API from starting: {failure}");
        Assert.Contains(host.Logs.Entries, e => e.Level >= LogLevel.Error);
        await AssertSeedsOnceMigratedAsync(host, database);
    }

    private static async Task AssertSeedsOnceMigratedAsync(DemoHost host, DemoDatabase database)
    {
        using (var beforeMigration = await host.LoginRawAsync(DemoData.AcmeOwnerEmail))
        {
            Assert.NotEqual(HttpStatusCode.OK, beforeMigration.StatusCode);
        }

        await database.MigrateAsync();
        Assert.Empty(await DemoDbReader.TenantsAsync(database));

        await host.SeedAsync();

        await DemoDatasetAssertions.AssertCompleteAsync(database);
        using var afterSeeding = await host.LoginRawAsync(DemoData.AcmeOwnerEmail);
        Assert.Equal(HttpStatusCode.OK, afterSeeding.StatusCode);
    }
}
