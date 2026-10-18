using Conversia.API.Configuration;
using Conversia.Domain.Enums;
using Conversia.Infrastructure.Configuration;
using Conversia.Infrastructure.Conversations;
using Conversia.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>
/// The close-by-inactivity job. The closer works across every tenant, so these tests live in the year 2020, far from the data of the other
/// suites: nothing of theirs is due, and the numbers asserted here are only about the conversations seeded here.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class ConversationLifecycleTests
{
    private static readonly DateTime Now = new(2020, 6, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime IdleStart = Now.AddDays(-8); // a conversation that began 8 days ago and said nothing since

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public ConversationLifecycleTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private Task<ChannelFixture> NewTenantAsync() => StartChannelAsync(_factory, _client);

    private static ConversationLifecycleOptions Options(int batchSize = 200, int afterDays = 7) => new(true, afterDays, 15, batchSize);

    private async Task<ConversationAutoCloser.ClosedBatch> CloseAsync(DateTime now, int batchSize = 200)
    {
        using var scope = _factory.Services.CreateScope();
        var closer = new ConversationAutoCloser(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(), Options(batchSize));
        return await closer.CloseBatchAsync(now);
    }

    // ---- When a conversation is idle ---------------------------------------------------------------------

    [Fact]
    public async Task AnIdleConversation_IsClosed_AtTheTimeOfTheRun()
    {
        using var f = await NewTenantAsync();
        var idle = await SeedConversationAsync(_factory.Services, f, IdleStart, [In(0), Out(60)]);

        await CloseAsync(Now);

        var (conversation, _) = await LoadAsync(_factory.Services, idle.ConversationId);
        Assert.Equal((ConversationStatus.Closed, (DateTime?)Now), (conversation.Status, conversation.ClosedAt));
    }

    [Fact]
    public async Task TheCutoff_IsStrict_AConversationExactlyAtTheLimitStaysOpen()
    {
        using var f = await NewTenantAsync();
        var atTheLimit = await SeedConversationAsync(_factory.Services, f, Now.AddDays(-7), [In(0)]);
        var justOver = await SeedConversationAsync(_factory.Services, f, Now.AddDays(-7).AddSeconds(-1), [In(0)]);
        var recent = await SeedConversationAsync(_factory.Services, f, Now.AddDays(-1), [In(0)]);

        await CloseAsync(Now);

        Assert.Equal(ConversationStatus.Open, (await LoadAsync(_factory.Services, atTheLimit.ConversationId)).Conversation.Status);
        Assert.Equal(ConversationStatus.Closed, (await LoadAsync(_factory.Services, justOver.ConversationId)).Conversation.Status);
        Assert.Equal(ConversationStatus.Open, (await LoadAsync(_factory.Services, recent.ConversationId)).Conversation.Status);
    }

    [Fact]
    public async Task TheAgeIsTheLastMessage_NotTheStart()
    {
        using var f = await NewTenantAsync();
        var longButActive = await SeedConversationAsync(_factory.Services, f, Now.AddDays(-30), [In(0), Out(60), In((int)TimeSpan.FromDays(29).TotalSeconds)]);

        await CloseAsync(Now);

        Assert.Equal(ConversationStatus.Open, (await LoadAsync(_factory.Services, longButActive.ConversationId)).Conversation.Status);
    }

    [Fact]
    public async Task TheLimitIsConfigurable()
    {
        using var f = await NewTenantAsync();
        var fiveDaysOld = await SeedConversationAsync(_factory.Services, f, Now.AddDays(-5), [In(0)]);

        using var scope = _factory.Services.CreateScope();
        var shortCloser = new ConversationAutoCloser(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(), Options(afterDays: 3));
        await shortCloser.CloseBatchAsync(Now);

        Assert.Equal(ConversationStatus.Closed, (await LoadAsync(_factory.Services, fiveDaysOld.ConversationId)).Conversation.Status);
    }

    [Fact]
    public async Task AClosedConversation_IsNotClosedAgain()
    {
        using var f = await NewTenantAsync();
        var idle = await SeedConversationAsync(_factory.Services, f, IdleStart, [In(0), Out(60)]);

        await CloseAsync(Now);
        await CloseAsync(Now.AddDays(3));

        var (conversation, opportunity) = await LoadAsync(_factory.Services, idle.ConversationId);
        Assert.Equal(Now, conversation.ClosedAt); // the first run's time, untouched by the second
        Assert.Equal(Now, opportunity.ClosedAt);
    }

    // ---- What happens to the opportunity -------------------------------------------------------------------

    [Fact]
    public async Task WhenTheBusinessSpokeLast_TheOpportunityIsLostForLackOfAnAnswer()
    {
        using var f = await NewTenantAsync();
        var ghosted = await SeedConversationAsync(_factory.Services, f, IdleStart, [In(0, "Hola, cuánto cuesta?"), Out(120, "Son $50.000. ¿Te agendo?")]);

        var batch = await CloseAsync(Now);

        var (_, opportunity) = await LoadAsync(_factory.Services, ghosted.ConversationId);
        Assert.Equal((OpportunityStage.Lost, LossReason.NoResponse, StageSource.Auto), (opportunity.Stage, opportunity.LossReason, opportunity.StageSource));
        Assert.Equal((Now, Now), (opportunity.ClosedAt, opportunity.StageChangedAt));
        Assert.True(batch.LostForNoResponse >= 1);
    }

    [Fact]
    public async Task WhenTheCustomerSpokeLast_TheOpportunityIsLeftAsItWas()
    {
        using var f = await NewTenantAsync();
        var unanswered = await SeedConversationAsync(_factory.Services, f, IdleStart, [In(0), Out(60), In(300, "¿Y entonces?")]);

        await CloseAsync(Now);

        var (conversation, opportunity) = await LoadAsync(_factory.Services, unanswered.ConversationId);
        Assert.Equal(ConversationStatus.Closed, conversation.Status);
        Assert.Equal((OpportunityStage.New, (LossReason?)null, (DateTime?)null), (opportunity.Stage, opportunity.LossReason, opportunity.ClosedAt));
    }

    [Fact]
    public async Task WhenBothSpokeInTheSameSecond_ItIsNotCountedAsGivenUp()
    {
        using var f = await NewTenantAsync();
        var tie = await SeedConversationAsync(_factory.Services, f, IdleStart, [Out(0), In(60), Out(60)]);

        await CloseAsync(Now);

        Assert.Equal(OpportunityStage.New, (await LoadAsync(_factory.Services, tie.ConversationId)).Opportunity.Stage);
    }

    [Fact]
    public async Task AConversationTheCustomerNeverWroteIn_IsClosed_ButItsOpportunityIsNotLost()
    {
        using var f = await NewTenantAsync();
        var broadcast = await SeedConversationAsync(_factory.Services, f, IdleStart, [Out(0, "Promoción de hoy")]);

        await CloseAsync(Now);

        var (conversation, opportunity) = await LoadAsync(_factory.Services, broadcast.ConversationId);
        Assert.Equal(ConversationStatus.Closed, conversation.Status);
        Assert.Equal(OpportunityStage.New, opportunity.Stage);
    }

    [Fact]
    public async Task ADecisionOfAPerson_AndAnOutcomeAlreadyReached_AreNeverChanged()
    {
        using var f = await NewTenantAsync();
        var manual = await SeedConversationAsync(_factory.Services, f, IdleStart, [In(0), Out(60)],
            configureOpportunity: o => o.SetManualOutcome(OpportunityStage.InFollowUp, null, null, IdleStart));
        var won = await SeedConversationAsync(_factory.Services, f, IdleStart, [In(0), Out(60)],
            configureOpportunity: o => o.SetManualOutcome(OpportunityStage.Won, null, null, IdleStart));
        var lost = await SeedConversationAsync(_factory.Services, f, IdleStart, [In(0), Out(60)],
            configureOpportunity: o => o.SetManualOutcome(OpportunityStage.Lost, LossReason.Price, "caro", IdleStart));

        await CloseAsync(Now);

        var (manualConversation, manualOpportunity) = await LoadAsync(_factory.Services, manual.ConversationId);
        Assert.Equal(ConversationStatus.Closed, manualConversation.Status); // the conversation closes...
        Assert.Equal((OpportunityStage.InFollowUp, StageSource.Manual), (manualOpportunity.Stage, manualOpportunity.StageSource)); // ...the decision stays
        Assert.Equal((OpportunityStage.Won, IdleStart), ((await LoadAsync(_factory.Services, won.ConversationId)).Opportunity.Stage, (await LoadAsync(_factory.Services, won.ConversationId)).Opportunity.ClosedAt));
        var lostOpportunity = (await LoadAsync(_factory.Services, lost.ConversationId)).Opportunity;
        Assert.Equal((OpportunityStage.Lost, LossReason.Price, "caro"), (lostOpportunity.Stage, lostOpportunity.LossReason, lostOpportunity.LossDetail));
    }

    // ---- Batches, tenants and locks ------------------------------------------------------------------------

    [Fact]
    public async Task ABatch_IsBounded_AndRunsFinishTheWork()
    {
        using var f = await NewTenantAsync();
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            ids.Add((await SeedConversationAsync(_factory.Services, f, IdleStart.AddMinutes(i), [In(0)])).ConversationId);
        }

        var first = await CloseAsync(Now, batchSize: 2);

        Assert.Equal(2, first.Closed);
        var closedAfterFirst = 0;
        foreach (var id in ids)
        {
            closedAfterFirst += (await LoadAsync(_factory.Services, id)).Conversation.Status == ConversationStatus.Closed ? 1 : 0;
        }

        Assert.Equal(2, closedAfterFirst);
        while ((await CloseAsync(Now, batchSize: 2)).Closed > 0)
        {
        }

        foreach (var id in ids)
        {
            Assert.Equal(ConversationStatus.Closed, (await LoadAsync(_factory.Services, id)).Conversation.Status);
        }
    }

    [Fact]
    public async Task EveryTenantsIdleConversations_AreClosed()
    {
        using var a = await NewTenantAsync();
        using var b = await NewTenantAsync();
        var inA = await SeedConversationAsync(_factory.Services, a, IdleStart, [In(0), Out(30)]);
        var inB = await SeedConversationAsync(_factory.Services, b, IdleStart, [In(0), Out(30)]);

        await CloseAsync(Now);

        Assert.Equal(OpportunityStage.Lost, (await LoadAsync(_factory.Services, inA.ConversationId)).Opportunity.Stage);
        Assert.Equal(OpportunityStage.Lost, (await LoadAsync(_factory.Services, inB.ConversationId)).Opportunity.Stage);
    }

    [Fact]
    public async Task AConversationLockedByAnotherTransaction_IsSkipped_AndClosedOnTheNextRun()
    {
        using var f = await NewTenantAsync();
        var busy = await SeedConversationAsync(_factory.Services, f, IdleStart, [In(0), Out(60)]);
        var free = await SeedConversationAsync(_factory.Services, f, IdleStart, [In(0), Out(60)]);

        using (var scope = _factory.Services.CreateScope())
        {
            var other = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await using var holder = await other.Database.BeginTransactionAsync();
            await other.Database.ExecuteSqlAsync($"SELECT 1 FROM \"Conversations\" WHERE \"Id\" = {busy.ConversationId} FOR UPDATE");

            await CloseAsync(Now); // does not wait for the lock

            Assert.Equal(ConversationStatus.Open, (await LoadAsync(_factory.Services, busy.ConversationId)).Conversation.Status);
            Assert.Equal(ConversationStatus.Closed, (await LoadAsync(_factory.Services, free.ConversationId)).Conversation.Status);
        }

        await CloseAsync(Now);

        Assert.Equal(ConversationStatus.Closed, (await LoadAsync(_factory.Services, busy.ConversationId)).Conversation.Status);
    }

    // ---- Ingestion and the closer never fight over a conversation ------------------------------------------

    [Fact]
    public async Task AMessageThatArrivesWhileTheCloserHoldsTheConversation_WaitsAndOpensANewConversationOnceItIsClosed()
    {
        await ClearQueueAsync(_factory.Services);
        using var f = await NewTenantAsync();
        var runner = RunnerOf(_factory.Services);
        await DeliverAsync(_client, f, Conversia.Tests.Shared.Dialog360Samples.InboundText("wamid.FIRST", Conversia.Tests.Shared.Dialog360Samples.T0));
        await DrainAsync(runner);
        var opened = Assert.Single((await SnapshotAsync(_factory.Services, f.TenantId)).Conversations);

        using var scope = _factory.Services.CreateScope();
        var closer = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await using var holder = await closer.Database.BeginTransactionAsync(); // "the closer" has the conversation locked, about to close it
        await closer.Database.ExecuteSqlAsync($"SELECT 1 FROM \"Conversations\" WHERE \"Id\" = {opened.Id} FOR UPDATE");

        await DeliverAsync(_client, f, Conversia.Tests.Shared.Dialog360Samples.InboundText("wamid.SECOND", Conversia.Tests.Shared.Dialog360Samples.T0 + 3600, text: "Sigo interesada"));
        var ingestion = Task.Run(() => DrainAsync(runner));
        await WaitUntilABackendWaitsOnALockAsync(_factory);
        Assert.False(ingestion.IsCompleted); // the ingestion is held behind the lock, it did not append to a conversation about to close

        await closer.Database.ExecuteSqlAsync(
            $"UPDATE \"Conversations\" SET \"Status\" = 'Closed', \"ClosedAt\" = {opened.LastMessageAt} WHERE \"Id\" = {opened.Id}");
        await holder.CommitAsync();
        await ingestion.WaitAsync(TimeSpan.FromSeconds(30));

        var snapshot = await SnapshotAsync(_factory.Services, f.TenantId);
        Assert.Equal(2, snapshot.Conversations.Count);
        var old = snapshot.Conversations.Single(c => c.Id == opened.Id);
        var fresh = snapshot.Conversations.Single(c => c.Id != opened.Id);
        Assert.Equal((ConversationStatus.Closed, ConversationStatus.Open), (old.Status, fresh.Status));
        Assert.Equal(["wamid.FIRST"], snapshot.Messages.Where(m => m.ConversationId == old.Id).Select(m => m.ProviderMessageId));
        Assert.Equal(["wamid.SECOND"], snapshot.Messages.Where(m => m.ConversationId == fresh.Id).Select(m => m.ProviderMessageId));
        Assert.Equal(2, snapshot.Opportunities.Count);
    }

    // Polls the server (not the clock) until some session is blocked waiting for a row lock; bounded by a deadline.
    private static async Task WaitUntilABackendWaitsOnALockAsync(CustomWebApplicationFactory factory)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        await using var connection = new NpgsqlConnection(factory.ServerConnectionString);
        await connection.OpenAsync();
        while (DateTime.UtcNow < deadline)
        {
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND wait_event IN ('transactionid', 'tuple') AND query LIKE '%FOR UPDATE%'", connection);
            if ((long)(await command.ExecuteScalarAsync())! > 0)
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("No session ever waited for the conversation lock.");
    }

    // ---- The hosted service and its configuration ----------------------------------------------------------

    [Fact]
    public async Task TheService_ClosesIdleConversationsAtStartup_WhenEnabled()
    {
        using var f = await NewTenantAsync();
        var idle = await SeedConversationAsync(_factory.Services, f, DateTime.UtcNow.AddDays(-10), [In(0), Out(60)]);
        var recent = await SeedConversationAsync(_factory.Services, f, DateTime.UtcNow.AddHours(-1), [In(0)]);
        var capture = new CapturingLoggerProvider();
        using var host = WebhookHost(_factory, new() { ["Conversations:AutoCloseEnabled"] = "true" }, configureLogging: logging => logging.AddProvider(capture));

        using var client = host.CreateClient(); // starts the host, and with it the service
        await capture.WhenLoggedAsync(e => e.Text.Contains("idle for more than 7 days")).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(ConversationStatus.Closed, (await LoadAsync(_factory.Services, idle.ConversationId)).Conversation.Status);
        Assert.Equal(ConversationStatus.Open, (await LoadAsync(_factory.Services, recent.ConversationId)).Conversation.Status);
        Assert.DoesNotContain(capture.Entries, e => e.Text.Contains(f.Channel.DisplayPhoneNumber) || e.Text.Contains("Hola"));
    }

    [Fact]
    public void TheService_IsNotRunningWhenDisabled_WhichIsHowTheTestHostsRun()
    {
        var disabled = _factory.Services.GetServices<IHostedService>().Select(s => s.GetType().Name);
        using var enabledHost = WebhookHost(_factory, new() { ["Conversations:AutoCloseEnabled"] = "true" });
        var enabled = enabledHost.Services.GetServices<IHostedService>().Select(s => s.GetType().Name);

        Assert.DoesNotContain("ConversationLifecycleService", disabled);
        Assert.Contains("ConversationLifecycleService", enabled);
    }

    [Fact]
    public void TheDefaults_AreSevenDays_AndBadValuesStopTheApiAtStartup()
    {
        static IConfiguration Config(params (string, string)[] values) => new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Item1, v.Item2))).Build();

        var defaults = ConversationServiceCollectionExtensions.GetValidatedLifecycleOptions(Config());
        Assert.Equal((true, 7, 15, 200), (defaults.Enabled, defaults.AfterDays, defaults.IntervalMinutes, defaults.BatchSize));

        var custom = ConversationServiceCollectionExtensions.GetValidatedLifecycleOptions(Config(("Conversations:AutoCloseAfterDays", "14"), ("Conversations:AutoCloseEnabled", "false")));
        Assert.Equal((false, 14), (custom.Enabled, custom.AfterDays));

        foreach (var (key, value) in new[]
                 {
                     ("Conversations:AutoCloseAfterDays", "0"), ("Conversations:AutoCloseAfterDays", "400"), ("Conversations:AutoCloseIntervalMinutes", "0"),
                     ("Conversations:AutoCloseBatchSize", "many"), ("Conversations:AutoCloseEnabled", "maybe"),
                 })
        {
            var ex = Assert.Throws<InvalidOperationException>(() => ConversationServiceCollectionExtensions.GetValidatedLifecycleOptions(Config((key, value))));
            Assert.Contains(key, ex.Message);
        }
    }

    [Fact]
    public void TheTimeZone_DefaultsToBogota_AndAnUnknownOneStopsTheApiAtStartupNamingTheKey()
    {
        static IConfiguration Config(params (string, string)[] values) => new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Item1, v.Item2))).Build();

        Assert.Equal("America/Bogota", ConversationServiceCollectionExtensions.GetValidatedTimeZone(Config()).Id);
        Assert.Equal("America/Mexico_City", ConversationServiceCollectionExtensions.GetValidatedTimeZone(Config(("Metrics:TimeZone", "America/Mexico_City"))).Id);

        var ex = Assert.Throws<InvalidOperationException>(() => ConversationServiceCollectionExtensions.GetValidatedTimeZone(Config(("Metrics:TimeZone", "Mars/Olympus_Mons"))));
        Assert.Contains("Metrics:TimeZone", ex.Message);
    }
}
