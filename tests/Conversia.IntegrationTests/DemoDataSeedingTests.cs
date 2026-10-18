using Conversia.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Conversia.IntegrationTests;

/// <summary>
/// What <c>IDemoDataSeeder.SeedAsync</c> creates, that each tenant is created all at once or not at all, and that
/// running it again — after the user changed or deleted demo data — creates and changes nothing. The class shares one
/// migrated database that is emptied before each test, so the seeder is called explicitly on a database in the state
/// "just migrated".
/// </summary>
public class DemoDataSeedingTests(CustomWebApplicationFactory container, DemoClassState state) : DemoDataTestBase(container, state)
{
    [Fact]
    public async Task SeedAsync_OnAMigratedEmptyDatabase_CreatesBothDemoTenantsCompletely()
    {
        var host = await StartEmptyHostAsync();
        Assert.Empty(await DemoDbReader.TenantsAsync(Db));

        await host.SeedAsync();

        await DemoDatasetAssertions.AssertCompleteAsync(Db);
    }

    [Fact]
    public async Task SeedAsync_StoresTheDemoPasswordOnlyAsAHashThatVerifiesForEveryAccount()
    {
        var host = await StartEmptyHostAsync();

        await host.SeedAsync();

        Assert.Equal(DemoData.AllEmails.Length, (await DemoDbReader.UsersAsync(Db)).Count);
        await DemoDatasetAssertions.AssertPasswordsHashedAsync(host, Db, DemoData.DefaultPassword);
    }

    [Fact]
    public async Task SeedAsync_GivesTheTwoTenantsTheSameSkuAsSeparateProducts()
    {
        var host = await StartEmptyHostAsync();

        await host.SeedAsync();

        var acme = await DemoDbReader.TenantIdAsync(Db, DemoData.AcmeTenantName);
        var globex = await DemoDbReader.TenantIdAsync(Db, DemoData.GlobexTenantName);
        var shared = (await DemoDbReader.ProductsAsync(Db)).Where(p => p.Sku == DemoData.SharedSku).ToList();
        Assert.Equal(new[] { acme, globex }.Order(), shared.Select(p => p.TenantId).Order());
        Assert.Equal(2, shared.Select(p => p.Id).Distinct().Count());
    }

    [Fact]
    public async Task SeedAsync_CalledAgain_CreatesAndChangesNothing()
    {
        var host = await StartEmptyHostAsync();
        await host.SeedAsync();
        var before = await DemoDbReader.SnapshotAsync(Db);
        Assert.NotEmpty(before);

        await host.SeedAsync();
        await host.SeedAsync();

        Assert.Equal(before, await DemoDbReader.SnapshotAsync(Db));
    }

    [Fact]
    public async Task SeedAsync_AfterTheDemoDataWasEditedAndDeleted_RestoresAndOverwritesNothing()
    {
        var host = await StartEmptyHostAsync();
        await host.SeedAsync();
        await EditAndDeleteDemoDataAsync();
        var before = await DemoDbReader.SnapshotAsync(Db);

        await host.SeedAsync();

        Assert.Equal(before, await DemoDbReader.SnapshotAsync(Db));
        Assert.Single(await DemoDbReader.TenantsAsync(Db, "Acme Renamed"));
        Assert.Empty(await DemoDbReader.TenantsAsync(Db, DemoData.AcmeTenantName));
    }

    [Fact]
    public async Task SeedAsync_WhenOnlyOneDemoTenantWasRemoved_RecreatesThatTenantAndLeavesTheOtherUntouched()
    {
        var host = await StartEmptyHostAsync();
        await host.SeedAsync();
        await Db.RemoveTenantAsync(await DemoDbReader.TenantIdAsync(Db, DemoData.GlobexTenantName));
        Assert.Single(await DemoDbReader.TenantsAsync(Db));
        var acmeBefore = await DemoDbReader.SnapshotAsync(Db);

        await host.SeedAsync();

        await DemoDatasetAssertions.AssertCompleteAsync(Db);
        var after = await DemoDbReader.SnapshotAsync(Db);
        Assert.All(acmeBefore, row => Assert.Contains(row, after));
    }

    // ---- Each tenant is seeded together or not at all ----------------------------------------------------------------

    [Fact]
    public async Task SeedAsync_WhenANonOwnerDemoEmailIsAlreadyTakenElsewhere_NeverLeavesAHalfSeededAcmeTenant()
    {
        var host = await StartEmptyHostAsync();

        foreach (var takenEmail in new[] { DemoData.AcmeManagerEmail, DemoData.AcmeViewerEmail, DemoData.AcmeNoAccessEmail })
        {
            await Db.WipeAsync();
            var squatter = await DemoDbReader.PlantSquatterAsync(Db, takenEmail);
            var squatterBefore = await DemoDbReader.UserAsync(Db, takenEmail);

            // The requirement fixes no outcome for the call itself (it may report the conflict or treat it as lost race);
            // it only forbids leaving anything half-done behind.
            await Record.ExceptionAsync(() => host.SeedAsync());

            Assert.True(
                (await DemoDbReader.TenantsAsync(Db, DemoData.AcmeTenantName)).Count == 0,
                $"Acme was seeded although {takenEmail} belongs to another tenant.");
            Assert.DoesNotContain(await DemoDbReader.UsersAsync(Db), u => u.TenantId != squatter.TenantId && u.Email.EndsWith("@acme-demo.example"));
            var globexIds = (await DemoDbReader.TenantsAsync(Db, DemoData.GlobexTenantName)).Select(t => t.Id).ToList();
            Assert.All(await DemoDbReader.ProductsAsync(Db), p => Assert.Contains(p.TenantId, globexIds));
            Assert.Equal(squatterBefore.PasswordHash, (await DemoDbReader.UserAsync(Db, takenEmail)).PasswordHash);
            Assert.Equal(squatter.TenantId, (await DemoDbReader.UserAsync(Db, takenEmail)).TenantId);
            await DemoDatasetAssertions.AssertNoHalfSeededTenantAsync(Db);

            // The refused attempt left nothing behind that could keep Acme from being seeded later (an orphan owner or
            // tenant would make every later run believe Acme already exists).
            await DemoDbReader.RemoveSquatterAsync(Db, squatter);
            await host.SeedAsync();
            await DemoDatasetAssertions.AssertCompleteAsync(Db);
        }
    }

    [Fact]
    public async Task SeedAsync_WhenAnotherTenantAlreadyOwnsTheAcmeOwnerEmail_SkipsAcmeWithoutErrorAndStillSeedsGlobex()
    {
        var host = await StartEmptyHostAsync();
        var squatter = await DemoDbReader.PlantSquatterAsync(Db, DemoData.AcmeOwnerEmail, isOwner: true);

        await host.SeedAsync();

        Assert.Empty(await DemoDbReader.TenantsAsync(Db, DemoData.AcmeTenantName));
        Assert.Equal(squatter.TenantId, (await DemoDbReader.UserAsync(Db, DemoData.AcmeOwnerEmail)).TenantId);
        Assert.Equal(
            new[] { DemoData.AcmeOwnerEmail, DemoData.GlobexOwnerEmail }.Order(),
            (await DemoDbReader.UsersAsync(Db)).Select(u => u.Email).Order());
        await DemoDatasetAssertions.AssertGlobexCompleteAsync(Db);
        Assert.Equal(DemoData.GlobexProductCount, (await DemoDbReader.ProductsAsync(Db)).Count);
    }

    [Fact]
    public async Task SeedAsync_WhenAnotherTenantAlreadyOwnsTheGlobexOwnerEmail_SkipsGlobexWithoutErrorAndStillSeedsAcme()
    {
        var host = await StartEmptyHostAsync();
        var squatter = await DemoDbReader.PlantSquatterAsync(Db, DemoData.GlobexOwnerEmail, isOwner: true);

        await host.SeedAsync();

        Assert.Empty(await DemoDbReader.TenantsAsync(Db, DemoData.GlobexTenantName));
        Assert.Equal(squatter.TenantId, (await DemoDbReader.UserAsync(Db, DemoData.GlobexOwnerEmail)).TenantId);
        await DemoDatasetAssertions.AssertAcmeCompleteAsync(Db);
        Assert.Equal(DemoData.AcmeProductCount, (await DemoDbReader.ProductsAsync(Db)).Count);
    }

    [Fact]
    public async Task SeedAsync_WithACancelledToken_IsCancelledOrCompleteButNeverHalfDone_AndALaterCallSeedsEverything()
    {
        var host = await StartEmptyHostAsync();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var failure = await Record.ExceptionAsync(() => host.SeedAsync(cancelled.Token));

        Assert.True(failure is null or OperationCanceledException, $"Cancellation must surface as OperationCanceledException, got {failure}.");
        await DemoDatasetAssertions.AssertNoHalfSeededTenantAsync(Db);

        await host.SeedAsync();

        await DemoDatasetAssertions.AssertCompleteAsync(Db);
    }

    // ---- helpers -----------------------------------------------------------------------------------------------------

    /// <summary>What a user of the demo might do: delete a product, change another, delete accounts, take a permission away, rename a tenant.</summary>
    private async Task EditAndDeleteDemoDataAsync()
    {
        await using var db = Db.NewContext();
        var acme = await db.Tenants.SingleAsync(t => t.Name == DemoData.AcmeTenantName);
        var products = await db.Products.IgnoreQueryFilters().Where(p => p.TenantId == acme.Id).OrderBy(p => p.Sku).ToListAsync();
        db.Products.Remove(products[0]);
        products[1].Update("Edited by the user", 123.45m, 7);

        var noAccess = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.Email == DemoData.AcmeNoAccessEmail);
        db.Users.Remove(noAccess);

        var manager = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.Email == DemoData.AcmeManagerEmail);
        var usersView = await db.UserPermissions.IgnoreQueryFilters()
            .SingleAsync(p => p.UserId == manager.Id && p.Permission == Conversia.Domain.Enums.Permission.UsersView);
        db.UserPermissions.Remove(usersView);

        await db.SaveChangesAsync();
        await db.Tenants.Where(t => t.Id == acme.Id).ExecuteUpdateAsync(set => set.SetProperty(t => t.Name, "Acme Renamed"));
    }
}
