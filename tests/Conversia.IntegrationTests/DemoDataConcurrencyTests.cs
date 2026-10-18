namespace Conversia.IntegrationTests;

/// <summary>
/// Seeding under contention: many callers of <c>SeedAsync</c> and several API instances starting at once on one
/// database. The unique indexes arbitrate; the losers treat the tenant as already seeded, so nobody throws and the
/// database ends up with exactly one of everything.
/// </summary>
public class DemoDataConcurrencyTests(CustomWebApplicationFactory container, DemoClassState state) : DemoDataTestBase(container, state)
{
    /// <summary>Starts all calls at the same instant (after every one of them is ready) and returns the failures, if any.</summary>
    private static async Task<List<Exception>> RunSimultaneouslyAsync(IReadOnlyList<Func<Task>> calls)
    {
        var gate = new TaskCompletionSource();
        var ready = new CountdownEvent(calls.Count);
        var tasks = calls.Select(call => Task.Run(async () =>
        {
            ready.Signal();
            await gate.Task;
            try
            {
                await call();
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        })).ToList();

        ready.Wait();
        gate.SetResult();
        return (await Task.WhenAll(tasks)).OfType<Exception>().ToList();
    }

    private static Task<List<Exception>> SeedSimultaneouslyAsync(DemoHost host, int callers)
        => RunSimultaneouslyAsync(Enumerable.Range(0, callers).Select<int, Func<Task>>(_ => () => host.SeedAsync()).ToList());

    private static void AssertNoFailures(List<Exception> failures)
        => Assert.True(failures.Count == 0, "Concurrent seeding must not throw, but: " + string.Join(" | ", failures.Select(f => ExceptionChain.Describe(f))));

    [Fact]
    public async Task ParallelSeedAsync_OnAFreshDatabase_CompleteWithoutErrorsAndLeaveExactlyOneOfEverything()
    {
        var host = await StartEmptyHostAsync();

        var failures = await SeedSimultaneouslyAsync(host, 16);

        AssertNoFailures(failures);
        await DemoDatasetAssertions.AssertCompleteAsync(Db);
    }

    [Fact]
    public async Task ParallelSeedAsync_RepeatedOnFreshDatabases_NeverProducesDuplicatesOrFailures()
    {
        var host = await StartEmptyHostAsync();

        for (var round = 0; round < 6; round++)
        {
            AssertNoFailures(await SeedSimultaneouslyAsync(host, 8));
            await DemoDatasetAssertions.AssertCompleteAsync(Db);
            await Db.WipeAsync();
        }
    }

    [Fact]
    public async Task ParallelSeedAsync_OnAnAlreadySeededDatabase_ChangesNothing()
    {
        var host = await StartEmptyHostAsync();
        await host.SeedAsync();
        var before = await DemoDbReader.SnapshotAsync(Db);

        AssertNoFailures(await SeedSimultaneouslyAsync(host, 12));

        Assert.Equal(before, await DemoDbReader.SnapshotAsync(Db));
    }

    [Fact]
    public async Task ParallelSeedAsync_WhenGlobexIsTakenByAnotherTenant_SeedsAcmeExactlyOnceAndNeverTouchesTheOtherTenant()
    {
        var host = await StartEmptyHostAsync();
        var squatter = await DemoDbReader.PlantSquatterAsync(Db, DemoData.GlobexOwnerEmail, isOwner: true);

        AssertNoFailures(await SeedSimultaneouslyAsync(host, 10));

        await DemoDatasetAssertions.AssertAcmeCompleteAsync(Db);
        Assert.Empty(await DemoDbReader.TenantsAsync(Db, DemoData.GlobexTenantName));
        Assert.Equal(squatter.TenantId, (await DemoDbReader.UserAsync(Db, DemoData.GlobexOwnerEmail)).TenantId);
        Assert.Equal(DemoData.AcmeProductCount, (await DemoDbReader.ProductsAsync(Db)).Count);
    }

    [Fact]
    public async Task ParallelSeedAsync_WhileANonOwnerDemoEmailIsTakenElsewhere_NeverLeavesAHalfSeededTenant()
    {
        var host = await StartEmptyHostAsync();
        var squatter = await DemoDbReader.PlantSquatterAsync(Db, DemoData.AcmeManagerEmail);

        // Whether the callers report the conflict is not fixed by the requirement; leaving anything half-done is forbidden.
        await SeedSimultaneouslyAsync(host, 10);

        await DemoDatasetAssertions.AssertNoHalfSeededTenantAsync(Db);
        Assert.Empty(await DemoDbReader.TenantsAsync(Db, DemoData.AcmeTenantName));

        // Nothing stays behind that would stop a later run from seeding Acme once the conflict is gone.
        await DemoDbReader.RemoveSquatterAsync(Db, squatter);
        AssertNoFailures(await SeedSimultaneouslyAsync(host, 4));
        await DemoDatasetAssertions.AssertCompleteAsync(Db);
    }

    [Fact]
    public async Task ApiInstancesStartingWhileSeedAsyncIsCalledDirectly_AllStartAndLeaveExactlyOneOfEverything()
    {
        var running = await StartEmptyHostAsync();
        var starting = Enumerable.Range(0, 4)
            .Select(i => NewHost(new DemoHostSettings("true", Environment: i % 2 == 0 ? "Development" : "Demo")))
            .ToList();
        var work = new List<Func<Task>> { () => running.SeedAsync(), () => running.SeedAsync() };
        work.AddRange(starting.Select<DemoHost, Func<Task>>(h => () => Task.Run(() => h.Start())));

        var failures = await RunSimultaneouslyAsync(work);

        AssertNoFailures(failures);
        Assert.All(starting, h => Assert.NotNull(h.Services));
        await DemoDatasetAssertions.AssertCompleteAsync(Db);
    }
}
