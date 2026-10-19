using System.Collections.Concurrent;
using System.Net.Http.Json;
using Conversia.API.Configuration;
using Conversia.Application.Classification;
using Conversia.Domain.Enums;
using Conversia.Infrastructure.Classification;
using Conversia.Infrastructure.Configuration;
using Conversia.Infrastructure.Persistence;
using Conversia.Tests.Shared;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>
/// The classification pipeline on a real PostgreSQL: ingestion flags conversations, the worker claims them after the debounce, classifies,
/// writes the opportunity and clears the flag. The worker's runner is driven by hand with a clock the test sets. Each test lives in an era
/// of its own far in the past (each one earlier than the last), so the runner, which claims across every tenant, never meets the rows of
/// the other tests; the numbers asserted are about the conversations seeded here.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class ConversationClassificationTests
{
    private static int _era;

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public ConversationClassificationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // A time nobody else uses: 1990 plus a distinct month for each call, going back as the run goes on.
    private static DateTime NewEra() => new DateTime(1990, 1, 1, 12, 0, 0, DateTimeKind.Utc).AddDays(-30 * Interlocked.Increment(ref _era));

    private Task<ChannelFixture> NewTenantAsync() => StartChannelAsync(_factory, _client);

    private static ClassificationOptions Options(int debounceSeconds = 60, int maxFailures = 5, int retryDelaySeconds = 0)
        => ClassificationOptions.Default with { DebounceSeconds = debounceSeconds, MaxFailures = maxFailures, RetryDelaySeconds = retryDelaySeconds };

    private ConversationClassificationRunner Runner(FixedTimeProvider clock, ClassificationOptions? options = null, IServiceProvider? services = null, ILogger<ConversationClassificationRunner>? logger = null)
        => new((services ?? _factory.Services).GetRequiredService<IServiceScopeFactory>(), options ?? Options(), clock, logger ?? NullLogger<ConversationClassificationRunner>.Instance);

    private static async Task<int> DrainClassificationAsync(ConversationClassificationRunner runner)
    {
        var handled = 0;
        while (await runner.ProcessNextAsync())
        {
            handled++;
        }

        return handled;
    }

    private static readonly Msg[] RefusedChat =
    [
        In(0, "Hola, cuánto cuesta la depilación de axilas?"),
        Out(60, "Son $60.000 la sesión."),
        In(120, "No gracias"),
    ];

    private Task<Seeded> SeedAsync(ChannelFixture f, DateTime start, IReadOnlyList<Msg>? messages = null, Action<Domain.Entities.Opportunity>? configure = null)
        => SeedConversationAsync(_factory.Services, f, start, messages ?? RefusedChat, configureOpportunity: configure, requestClassification: true);

    // ---- Ingestion flags what changed -------------------------------------------------------------------------

    [Fact]
    public async Task IngestionFlagsEveryConversationThatReceivesMessages_AndGivesItAFreshStart()
    {
        await ClearQueueAsync(_factory.Services);
        using var f = await NewTenantAsync();
        var webhookRunner = RunnerOf(_factory.Services);
        await DeliverAsync(_client, f, Dialog360Samples.InboundText("wamid.A", Dialog360Samples.T0));
        await DrainAsync(webhookRunner);
        var first = Assert.Single((await SnapshotAsync(_factory.Services, f.TenantId)).Conversations);
        Assert.Equal((true, 0), (first.ClassificationDirty, first.ClassificationFailures));
        Assert.NotNull(first.ClassificationRequestedAt);

        // The worker gave up on it (3 failures) and cleared nothing; a new message must give it a fresh start.
        using (var scope = _factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.ExecuteSqlAsync(
                $"UPDATE \"Conversations\" SET \"ClassificationFailures\" = 3, \"ClassificationAttemptedAt\" = now() WHERE \"Id\" = {first.Id}");
        }

        await DeliverAsync(_client, f, Dialog360Samples.InboundText("wamid.B", Dialog360Samples.T0 + 30, text: "¿Y el precio?"));
        await DrainAsync(webhookRunner);

        var second = Assert.Single((await SnapshotAsync(_factory.Services, f.TenantId)).Conversations);
        Assert.Equal((true, 0, (DateTime?)null), (second.ClassificationDirty, second.ClassificationFailures, second.ClassificationAttemptedAt));
        Assert.True(second.ClassificationRequestedAt > first.ClassificationRequestedAt);
    }

    [Fact]
    public async Task ADeliveryWithNothingNew_FlagsNothing()
    {
        await ClearQueueAsync(_factory.Services);
        using var f = await NewTenantAsync();
        var webhookRunner = RunnerOf(_factory.Services);
        await DeliverAsync(_client, f, Dialog360Samples.InboundText("wamid.A", Dialog360Samples.T0));
        await DrainAsync(webhookRunner);
        var conversation = Assert.Single((await SnapshotAsync(_factory.Services, f.TenantId)).Conversations);
        using (var scope = _factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.ExecuteSqlAsync(
                $"UPDATE \"Conversations\" SET \"ClassificationDirty\" = false WHERE \"Id\" = {conversation.Id}");
        }

        // The same message inside another body (a different hash): already stored, so the conversation did not change. Then receipts only.
        await DeliverAsync(_client, f, Dialog360Samples.InboundText("wamid.A", Dialog360Samples.T0, name: "Laura G."));
        await DeliverAsync(_client, f, Dialog360Samples.StatusesOnly());
        await DrainAsync(webhookRunner);

        Assert.False(Assert.Single((await SnapshotAsync(_factory.Services, f.TenantId)).Conversations).ClassificationDirty);
    }

    // ---- The worker ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task AConversationIsClassifiedOnlyOnceItHasBeenQuietForTheDebounce()
    {
        using var f = await NewTenantAsync();
        var start = NewEra();
        var seeded = await SeedAsync(f, start);
        var lastMessage = start.AddSeconds(120);
        var clock = new FixedTimeProvider(lastMessage.AddSeconds(59));
        var runner = Runner(clock);

        Assert.False(await runner.ProcessNextAsync()); // 59 s of quiet: too soon
        Assert.True((await LoadAsync(_factory.Services, seeded.ConversationId)).Conversation.ClassificationDirty);

        clock.Advance(TimeSpan.FromSeconds(1)); // 60 s: due ("older than" the debounce is inclusive of the boundary)
        Assert.True(await runner.ProcessNextAsync());

        Assert.False((await LoadAsync(_factory.Services, seeded.ConversationId)).Conversation.ClassificationDirty);
        Assert.False(await runner.ProcessNextAsync());
    }

    [Fact]
    public async Task TheQuietPeriodCountsFromTheArrivalOfTheRequest_NotFromTheTimeWrittenOnTheMessage()
    {
        // A message the provider delivers late (or replays) carries an old SentAt: the conversation must still wait for the debounce from now.
        using var f = await NewTenantAsync();
        var start = NewEra();
        var seeded = await SeedAsync(f, start);
        var arrival = start.AddDays(10);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var tracked = await db.Conversations.IgnoreQueryFilters().SingleAsync(c => c.Id == seeded.ConversationId);
            tracked.RequestClassification(arrival);
            await db.SaveChangesAsync();
        }

        var clock = new FixedTimeProvider(arrival.AddSeconds(59));
        var runner = Runner(clock);

        Assert.False(await runner.ProcessNextAsync()); // the last message is ten days old, but it arrived 59 s ago
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(await runner.ProcessNextAsync());
        Assert.False((await LoadAsync(_factory.Services, seeded.ConversationId)).Conversation.ClassificationDirty);
    }

    [Fact]
    public async Task AFailureIsCountedOnlyForTheRequestItWasMadeFor()
    {
        using var f = await NewTenantAsync();
        var start = NewEra();
        var seeded = await SeedAsync(f, start);
        var claimed = (await LoadAsync(_factory.Services, seeded.ConversationId)).Conversation.ClassificationRequestedAt;
        var runner = Runner(new FixedTimeProvider(start.AddDays(1)));

        // A new message re-requests the classification (zero failures, no delay) while the attempt for the old request is failing.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var tracked = await db.Conversations.IgnoreQueryFilters().SingleAsync(c => c.Id == seeded.ConversationId);
            tracked.RequestClassification(start.AddHours(2));
            await db.SaveChangesAsync();
        }

        await runner.RecordFailureAsync(seeded.ConversationId, claimed, f.TenantId, new InvalidOperationException("late failure"));

        var untouched = (await LoadAsync(_factory.Services, seeded.ConversationId)).Conversation;
        Assert.Equal((true, 0, (DateTime?)null), (untouched.ClassificationDirty, untouched.ClassificationFailures, untouched.ClassificationAttemptedAt));

        // The failure of the current request is counted as always.
        await runner.RecordFailureAsync(seeded.ConversationId, untouched.ClassificationRequestedAt, f.TenantId, new InvalidOperationException("current failure"));

        var counted = (await LoadAsync(_factory.Services, seeded.ConversationId)).Conversation;
        Assert.Equal(1, counted.ClassificationFailures);
        Assert.NotNull(counted.ClassificationAttemptedAt);
    }

    [Fact]
    public async Task TheWorker_WritesWhatItReadToTheOpportunity_AndClearsTheFlag()
    {
        using var f = await NewTenantAsync();
        var start = NewEra();
        var seeded = await SeedAsync(f, start);
        var now = start.AddHours(1);

        await DrainClassificationAsync(Runner(new FixedTimeProvider(now)));

        var (conversation, opportunity) = await LoadAsync(_factory.Services, seeded.ConversationId);
        Assert.Equal((false, 0, (DateTime?)now), (conversation.ClassificationDirty, conversation.ClassificationFailures, conversation.ClassificationAttemptedAt));
        Assert.Equal((OpportunityStage.Lost, LossReason.NotInterested, StageSource.Auto), (opportunity.Stage, opportunity.LossReason, opportunity.StageSource));
        Assert.Null(opportunity.StageChangedBy); // the classifier is not a user
        Assert.Equal(("axilas", ObjectionType.None, now, now, now), (opportunity.ServiceInterest, opportunity.Objection, opportunity.ClassifiedAt, opportunity.ClosedAt, opportunity.StageChangedAt));
        Assert.InRange(opportunity.ClassificationConfidence!.Value, 0.5m, 1m);
        Assert.Contains("outcome.lost.refusal", opportunity.ClassificationRules!);
    }

    [Fact]
    public async Task TheApiShowsWhatTheClassifierConcluded_WithoutCustomerText()
    {
        using var f = await NewTenantAsync();
        var start = NewEra();
        var seeded = await SeedAsync(f, start);
        var before = (await f.Tenant.Owner.Client.GetFromJsonAsync<Conversia.Application.DTOs.Conversations.ConversationDetailResponse>($"/api/conversations/{seeded.ConversationId}"))!.Opportunity;
        Assert.Equal((null, null, null), (before.ClassificationConfidence, before.ClassificationRules, before.ClassifiedAt));

        await DrainClassificationAsync(Runner(new FixedTimeProvider(start.AddHours(1))));

        var opportunity = (await f.Tenant.Owner.Client.GetFromJsonAsync<Conversia.Application.DTOs.Conversations.ConversationDetailResponse>($"/api/conversations/{seeded.ConversationId}"))!.Opportunity;
        Assert.Equal(("Lost", "Auto", "NotInterested", "axilas"), (opportunity.Stage, opportunity.StageSource, opportunity.LossReason, opportunity.ServiceInterest));
        Assert.InRange(opportunity.ClassificationConfidence!.Value, 0.5m, 1m);
        Assert.Contains("outcome.lost.refusal", opportunity.ClassificationRules!);
        Assert.DoesNotContain("gracias", opportunity.ClassificationRules!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(start.AddHours(1), opportunity.ClassifiedAt);
    }

    [Fact]
    public async Task NothingTheCustomerWroteIsStoredOutsideTheMessages()
    {
        using var f = await NewTenantAsync();
        var start = NewEra();
        var seeded = await SeedAsync(f, start, [In(0, "Hola, cuánto cuesta la depilación de axilas? Soy Marcela Zuluaga, 3009998877"), Out(60, "Son $60.000"), In(120, "No gracias, marcela.zuluaga@example.test")]);

        await DrainClassificationAsync(Runner(new FixedTimeProvider(start.AddHours(1))));

        var (_, opportunity) = await LoadAsync(_factory.Services, seeded.ConversationId);
        var stored = string.Join('|', opportunity.ServiceInterest, opportunity.InterestReason, opportunity.ClassificationRules, opportunity.LossDetail);
        foreach (var secret in new[] { "Marcela", "Zuluaga", "3009998877", "example.test", "gracias", "cuesta" })
        {
            Assert.DoesNotContain(secret, stored, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task AManualStage_IsNeverOverwritten_ButTheDescriptiveFieldsStillUpdate()
    {
        using var f = await NewTenantAsync();
        var start = NewEra();
        var seeded = await SeedAsync(f, start, configure: o => o.SetManualOutcome(OpportunityStage.Won, null, null, start));

        await DrainClassificationAsync(Runner(new FixedTimeProvider(start.AddHours(1))));

        var (_, opportunity) = await LoadAsync(_factory.Services, seeded.ConversationId);
        Assert.Equal((OpportunityStage.Won, StageSource.Manual, start), (opportunity.Stage, opportunity.StageSource, opportunity.ClosedAt));
        Assert.Null(opportunity.LossReason);
        Assert.Equal("axilas", opportunity.ServiceInterest);
        Assert.NotNull(opportunity.ClassifiedAt);
    }

    [Fact]
    public async Task APersonsDecisionMadeWhileTheWorkerWasWaiting_Wins()
    {
        using var f = await NewTenantAsync();
        var start = NewEra();
        var seeded = await SeedAsync(f, start);
        var decided = await f.Tenant.Owner.Client.PutAsync(
            $"/api/conversations/{seeded.ConversationId}/opportunity",
            new StringContent("{\"stage\":\"InFollowUp\"}", System.Text.Encoding.UTF8, "application/json"));
        decided.EnsureSuccessStatusCode();

        await DrainClassificationAsync(Runner(new FixedTimeProvider(start.AddHours(1))));

        var (_, opportunity) = await LoadAsync(_factory.Services, seeded.ConversationId);
        Assert.Equal((OpportunityStage.InFollowUp, StageSource.Manual), (opportunity.Stage, opportunity.StageSource));
    }

    [Fact]
    public async Task ClassifyingAgain_ChangesNothingButTheTimeOfTheClassification()
    {
        using var f = await NewTenantAsync();
        var start = NewEra();
        var seeded = await SeedAsync(f, start);
        var first = new FixedTimeProvider(start.AddHours(1));
        await DrainClassificationAsync(Runner(first));
        var (_, before) = await LoadAsync(_factory.Services, seeded.ConversationId);

        using (var scope = _factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.ExecuteSqlAsync(
                $"UPDATE \"Conversations\" SET \"ClassificationDirty\" = true WHERE \"Id\" = {seeded.ConversationId}");
        }

        await DrainClassificationAsync(Runner(new FixedTimeProvider(start.AddHours(5))));
        var (conversation, after) = await LoadAsync(_factory.Services, seeded.ConversationId);

        Assert.Equal(
            (before.Stage, before.StageSource, before.StageChangedAt, before.ClosedAt, before.LossReason, before.Objection, before.ServiceInterest, before.InterestReason, before.ClassificationConfidence, before.ClassificationRules),
            (after.Stage, after.StageSource, after.StageChangedAt, after.ClosedAt, after.LossReason, after.Objection, after.ServiceInterest, after.InterestReason, after.ClassificationConfidence, after.ClassificationRules));
        Assert.Equal(start.AddHours(5), after.ClassifiedAt);
        Assert.False(conversation.ClassificationDirty);
    }

    [Fact]
    public async Task ANewMessageReflagsTheConversation_AndTheNextReadingFollowsTheConversation()
    {
        await ClearQueueAsync(_factory.Services);
        using var f = await NewTenantAsync();
        var webhookRunner = RunnerOf(_factory.Services);
        var t0 = Dialog360Samples.T0;
        await DeliverAsync(_client, f, Dialog360Samples.InboundText("wamid.1", t0, text: "Hola, cuánto cuesta la depilación de axilas?"));
        await DeliverAsync(_client, f, Dialog360Samples.OutboundEcho("wamid.2", t0 + 60, text: "Son $60.000."));
        await DeliverAsync(_client, f, Dialog360Samples.InboundText("wamid.3", t0 + 120, text: "No gracias"));
        await DrainAsync(webhookRunner);
        // The classifier claims by arrival time (the real clock), so the fake clock must be ahead of the real one,
        // not ahead of the fixed sample timestamps.
        var afterTheWorld = new FixedTimeProvider(DateTime.UtcNow.AddDays(1));
        var runner = Runner(afterTheWorld, Options(debounceSeconds: 0));
        await DrainClassificationAsync(runner);
        var conversationId = Assert.Single((await SnapshotAsync(_factory.Services, f.TenantId)).Conversations).Id;
        Assert.Equal(OpportunityStage.Lost, (await LoadAsync(_factory.Services, conversationId)).Opportunity.Stage);

        await DeliverAsync(_client, f, Dialog360Samples.InboundText("wamid.4", t0 + 600, text: "Perdón, sí quiero agendar mi cita"));
        await DrainAsync(webhookRunner);
        Assert.True((await LoadAsync(_factory.Services, conversationId)).Conversation.ClassificationDirty);
        await DrainClassificationAsync(runner);

        var (conversation, opportunity) = await LoadAsync(_factory.Services, conversationId);
        Assert.False(conversation.ClassificationDirty);
        Assert.Equal((OpportunityStage.InFollowUp, (LossReason?)null, (DateTime?)null), (opportunity.Stage, opportunity.LossReason, opportunity.ClosedAt));
    }

    // ---- Several tenants, several workers ----------------------------------------------------------------------

    private sealed class RecordingClassifier(IConversationClassifier inner, TimeSpan? delay = null) : IConversationClassifier
    {
        public ConcurrentBag<ConversationSnapshot> Seen { get; } = [];

        public async Task<ClassificationResult> ClassifyAsync(ConversationSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            Seen.Add(snapshot);
            if (delay is { } wait)
            {
                await Task.Delay(wait, cancellationToken);
            }

            return await inner.ClassifyAsync(snapshot, cancellationToken);
        }
    }

    private (WebApplicationFactory<Program> Host, RecordingClassifier Classifier) HostWith(Func<IConversationClassifier, IConversationClassifier> wrap)
    {
        RecordingClassifier? recording = null;
        var host = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            var rules = RuleBasedConversationClassifier.CreateDefault();
            services.AddSingleton<IConversationClassifier>(_ => (recording = (RecordingClassifier)wrap(rules))!);
        }));
        _ = host.Services.GetRequiredService<IConversationClassifier>(); // creates the recording classifier
        return (host, recording!);
    }

    [Fact]
    public async Task TwoTenants_AreReadWithTheirOwnMessagesOnly_AndEachGetsItsOwnResult()
    {
        using var a = await NewTenantAsync();
        using var b = await NewTenantAsync();
        var start = NewEra();
        var inA = await SeedAsync(a, start, [In(0, "Quiero agendar axilas"), Out(30, "¿Te agendo el sábado?"), In(60, "Listo, confirmo")]);
        var inB = await SeedAsync(b, start, [In(0, "Hola, quiero info de bikini"), Out(30, "$90.000"), In(60, "Muy caro, no gracias")]);
        var (host, recording) = HostWith(rules => new RecordingClassifier(rules));
        using var _ = host;

        await DrainClassificationAsync(Runner(new FixedTimeProvider(start.AddHours(1)), services: host.Services));

        Assert.Equal((OpportunityStage.Won, "axilas"), ((await LoadAsync(_factory.Services, inA.ConversationId)).Opportunity.Stage, (await LoadAsync(_factory.Services, inA.ConversationId)).Opportunity.ServiceInterest));
        var lost = (await LoadAsync(_factory.Services, inB.ConversationId)).Opportunity;
        Assert.Equal((OpportunityStage.Lost, LossReason.Price, "bikini"), (lost.Stage, lost.LossReason, lost.ServiceInterest));
        var snapshotA = recording.Seen.Single(s => s.ConversationId == inA.ConversationId);
        var snapshotB = recording.Seen.Single(s => s.ConversationId == inB.ConversationId);
        Assert.DoesNotContain(snapshotA.Messages, m => m.Text!.Contains("bikini"));
        Assert.DoesNotContain(snapshotB.Messages, m => m.Text!.Contains("axilas"));
        Assert.Equal("Sucursal", snapshotA.BranchName!.Split(' ')[0]);
        Assert.Equal(3, snapshotA.Messages.Count);
    }

    [Fact]
    public async Task SeveralWorkersAtOnce_ClassifyEveryConversationExactlyOnce()
    {
        using var f = await NewTenantAsync();
        var start = NewEra();
        var ids = new List<Guid>();
        for (var i = 0; i < 16; i++)
        {
            ids.Add((await SeedAsync(f, start.AddMinutes(i))).ConversationId);
        }

        var (host, recording) = HostWith(rules => new RecordingClassifier(rules, delay: TimeSpan.FromMilliseconds(20)));
        using var _ = host;
        var clock = new FixedTimeProvider(start.AddDays(1));

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => DrainClassificationAsync(Runner(clock, services: host.Services)))));

        foreach (var id in ids)
        {
            Assert.Single(recording.Seen, s => s.ConversationId == id);
            Assert.False((await LoadAsync(_factory.Services, id)).Conversation.ClassificationDirty);
        }
    }

    [Fact]
    public async Task AConversationHeldByAnotherTransaction_IsSkippedNotWaitedFor_AndClassifiedOnceReleased()
    {
        using var f = await NewTenantAsync();
        var start = NewEra();
        var busy = await SeedAsync(f, start);
        var free = await SeedAsync(f, start.AddMinutes(1));
        var runner = Runner(new FixedTimeProvider(start.AddDays(1)));

        using (var scope = _factory.Services.CreateScope())
        {
            var other = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await using var holder = await other.Database.BeginTransactionAsync();
            await other.Database.ExecuteSqlAsync($"SELECT 1 FROM \"Conversations\" WHERE \"Id\" = {busy.ConversationId} FOR UPDATE");

            await DrainClassificationAsync(runner);

            Assert.True((await LoadAsync(_factory.Services, busy.ConversationId)).Conversation.ClassificationDirty);
            Assert.False((await LoadAsync(_factory.Services, free.ConversationId)).Conversation.ClassificationDirty);
        }

        await DrainClassificationAsync(runner);

        Assert.False((await LoadAsync(_factory.Services, busy.ConversationId)).Conversation.ClassificationDirty);
    }

    [Fact]
    public async Task OnlyTheLatestMessagesAreRead_InConversationOrder()
    {
        using var f = await NewTenantAsync();
        var start = NewEra();
        var count = ConversationClassificationRunner.MaxMessagesPerConversation + 5;
        var messages = Enumerable.Range(0, count).Select(i => i % 2 == 0 ? In(i, $"mensaje {i}") : Out(i, $"respuesta {i}")).ToList();
        var seeded = await SeedAsync(f, start, messages);
        var (host, recording) = HostWith(rules => new RecordingClassifier(rules));
        using var _ = host;

        await DrainClassificationAsync(Runner(new FixedTimeProvider(start.AddDays(1)), services: host.Services));

        var snapshot = recording.Seen.Single(s => s.ConversationId == seeded.ConversationId);
        Assert.Equal(ConversationClassificationRunner.MaxMessagesPerConversation, snapshot.Messages.Count);
        Assert.Equal(snapshot.Messages.OrderBy(m => m.SentAt).Select(m => m.Text), snapshot.Messages.Select(m => m.Text));
        Assert.Equal($"respuesta {count - 1}", snapshot.Messages[^1].Text!.Replace("mensaje", "respuesta")); // the newest one is in
        Assert.Equal("mensaje 5", snapshot.Messages[0].Text!.Replace("respuesta", "mensaje")); // the oldest five are out
    }

    // ---- Failures ------------------------------------------------------------------------------------------------

    private sealed class FailingClassifier : IConversationClassifier
    {
        public int Calls;

        public Task<ClassificationResult> ClassifyAsync(ConversationSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            throw new InvalidOperationException("the classifier broke for customer 573009998877 who wrote 'quiero axilas'");
        }
    }

    private (WebApplicationFactory<Program> Host, FailingClassifier Classifier) FailingHost()
    {
        var failing = new FailingClassifier();
        var host = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services => services.AddSingleton<IConversationClassifier>(failing)));
        return (host, failing);
    }

    [Fact]
    public async Task AfterMaxFailures_TheConversationIsNotTriedAgain_UntilANewMessageGivesItAFreshStart()
    {
        using var f = await NewTenantAsync();
        var start = NewEra();
        var seeded = await SeedAsync(f, start);
        var (host, failing) = FailingHost();
        using var _ = host;
        var capture = new CapturingLoggerProvider();
        using var loggers = LoggerFactory.Create(b => b.AddProvider(capture));
        var runner = Runner(new FixedTimeProvider(start.AddDays(1)), Options(maxFailures: 3), host.Services, loggers.CreateLogger<ConversationClassificationRunner>());

        var handled = await DrainClassificationAsync(runner);

        Assert.Equal((3, 3), (handled, failing.Calls));
        var (conversation, opportunity) = await LoadAsync(_factory.Services, seeded.ConversationId);
        Assert.Equal((true, 3, OpportunityStage.New), (conversation.ClassificationDirty, conversation.ClassificationFailures, opportunity.Stage));
        Assert.Null(opportunity.ClassifiedAt);
        Assert.False(await runner.ProcessNextAsync()); // stopped: not claimed any more
        Assert.Equal(3, failing.Calls);

        // The warnings say what happened without the customer, the number or the text: the exception's message is never logged.
        var warnings = capture.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Text).ToList();
        Assert.All(warnings, w => Assert.Contains("InvalidOperationException", w));
        Assert.Equal(3, warnings.Count);
        Assert.Contains(warnings, w => w.Contains("will not be tried again") && w.Contains(seeded.ConversationId.ToString()));
        Assert.All(capture.Entries, e =>
        {
            Assert.DoesNotContain("573009998877", e.Text);
            Assert.DoesNotContain("quiero axilas", e.Text);
        });

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var tracked = await db.Conversations.IgnoreQueryFilters().SingleAsync(c => c.Id == seeded.ConversationId);
            tracked.RequestClassification(start.AddHours(3)); // what ingestion does with a new message
            await db.SaveChangesAsync();
        }

        Assert.True(await runner.ProcessNextAsync());
        Assert.Equal((4, 1), (failing.Calls, (await LoadAsync(_factory.Services, seeded.ConversationId)).Conversation.ClassificationFailures));
    }

    [Fact]
    public async Task AFailedConversation_WaitsOutTheRetryDelay_AndDoesNotBlockTheOthers()
    {
        using var f = await NewTenantAsync();
        var start = NewEra();
        var poison = await SeedAsync(f, start);
        var healthy = await SeedAsync(f, start.AddMinutes(1));
        var failingFor = poison.ConversationId;
        var host = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddSingleton<IConversationClassifier>(new SelectiveClassifier(failingFor))));
        using var _ = host;
        var clock = new FixedTimeProvider(start.AddDays(1));
        var runner = Runner(clock, Options(retryDelaySeconds: 60), host.Services);

        await DrainClassificationAsync(runner); // poison fails once and waits; healthy is classified

        Assert.Equal((1, true), ((await LoadAsync(_factory.Services, poison.ConversationId)).Conversation.ClassificationFailures, (await LoadAsync(_factory.Services, poison.ConversationId)).Conversation.ClassificationDirty));
        Assert.False((await LoadAsync(_factory.Services, healthy.ConversationId)).Conversation.ClassificationDirty);
        Assert.False(await runner.ProcessNextAsync()); // still waiting out the delay

        clock.Advance(TimeSpan.FromSeconds(61));
        Assert.True(await runner.ProcessNextAsync());
        Assert.Equal(2, (await LoadAsync(_factory.Services, poison.ConversationId)).Conversation.ClassificationFailures);
    }

    private sealed class SelectiveClassifier(Guid failFor) : IConversationClassifier
    {
        private readonly RuleBasedConversationClassifier _rules = RuleBasedConversationClassifier.CreateDefault();

        public Task<ClassificationResult> ClassifyAsync(ConversationSnapshot snapshot, CancellationToken cancellationToken = default)
            => snapshot.ConversationId == failFor ? throw new InvalidOperationException("broken") : Task.FromResult(_rules.Classify(snapshot));
    }

    // ---- Backlog ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheBacklog_CountsWhatWaitsAndWhatWasGivenUpOn()
    {
        using var f = await NewTenantAsync();
        var start = NewEra();
        var waiting = await SeedAsync(f, start);
        var stopped = await SeedAsync(f, start.AddMinutes(1));
        using (var scope = _factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.ExecuteSqlAsync(
                $"UPDATE \"Conversations\" SET \"ClassificationFailures\" = 5 WHERE \"Id\" = {stopped.ConversationId}");
        }

        var monitor = new ClassificationBacklogMonitor(_factory.Services.GetRequiredService<IServiceScopeFactory>(), Options(), new FixedTimeProvider(DateTime.UtcNow));
        var backlog = await monitor.GetBacklogAsync();

        Assert.True(backlog.Waiting >= 1);
        Assert.True(backlog.Stopped >= 1);
        Assert.NotNull(backlog.OldestWaitingAge);
        Assert.NotEqual(Guid.Empty, waiting.ConversationId);
    }

    [Fact]
    public async Task TheBacklogReporter_LogsAtMostOncePerMinute_WithoutCustomerData()
    {
        using var f = await NewTenantAsync();
        await SeedAsync(f, NewEra(), [In(0, "Hola soy Marcela 3009998877")]);
        var clock = new FixedTimeProvider(DateTime.UtcNow);
        var capture = new CapturingLoggerProvider();
        using var loggers = LoggerFactory.Create(b => b.AddProvider(capture));
        var monitor = new ClassificationBacklogMonitor(_factory.Services.GetRequiredService<IServiceScopeFactory>(), Options(), clock);
        var reporter = new ClassificationBacklogReporter(monitor, clock, loggers.CreateLogger<ClassificationBacklogReporter>());

        await reporter.ReportIfDueAsync();
        await reporter.ReportIfDueAsync();
        clock.Advance(TimeSpan.FromSeconds(30));
        await reporter.ReportIfDueAsync();
        Assert.Single(capture.Entries, e => e.Text.StartsWith("Classification backlog:"));

        clock.Advance(TimeSpan.FromSeconds(31));
        await reporter.ReportIfDueAsync();
        Assert.Equal(2, capture.Entries.Count(e => e.Text.StartsWith("Classification backlog:")));
        Assert.All(capture.Entries, e => Assert.DoesNotContain("Marcela", e.Text));
    }

    // ---- The hosted service and its configuration ------------------------------------------------------------------

    [Fact]
    public async Task TheService_ClassifiesInTheBackground_WhenEnabled()
    {
        using var f = await NewTenantAsync();
        var seeded = await SeedConversationAsync(_factory.Services, f, DateTime.UtcNow.AddHours(-1), RefusedChat, requestClassification: true);
        var capture = new CapturingLoggerProvider();
        using var host = WebhookHost(
            _factory,
            new() { ["Classification:Enabled"] = "true", ["Classification:DebounceSeconds"] = "0", ["Classification:PollIntervalMilliseconds"] = "20" },
            configureLogging: logging =>
            {
                logging.Services.Configure<LoggerFilterOptions>(o => o.Rules.Clear());
                logging.SetMinimumLevel(LogLevel.Debug);
                logging.AddProvider(capture);
            });

        using var client = host.CreateClient(); // starts the host, and with it the worker
        await capture.WhenLoggedAsync(e => e.Text.Contains($"Conversation {seeded.ConversationId} classified")).WaitAsync(TimeSpan.FromSeconds(30));

        var (conversation, opportunity) = await LoadAsync(_factory.Services, seeded.ConversationId);
        Assert.False(conversation.ClassificationDirty);
        Assert.Equal(OpportunityStage.Lost, opportunity.Stage);
    }

    [Fact]
    public void TheService_IsNotRunningWhenDisabled_WhichIsHowTheTestHostsRun()
    {
        var disabled = _factory.Services.GetServices<IHostedService>().Select(s => s.GetType().Name);
        using var enabledHost = WebhookHost(_factory, new() { ["Classification:Enabled"] = "true" });
        var enabled = enabledHost.Services.GetServices<IHostedService>().Select(s => s.GetType().Name);

        Assert.DoesNotContain("ConversationClassificationService", disabled);
        Assert.Contains("ConversationClassificationService", enabled);
    }

    private static IConfiguration Config(params (string, string)[] values)
        => new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Item1, v.Item2))).Build();

    [Fact]
    public void TheDefaults_AreTheDocumentedOnes_AndBadValuesStopTheApiAtStartupNamingTheKey()
    {
        var defaults = ClassificationServiceCollectionExtensions.GetValidatedOptions(Config());
        Assert.Equal((true, "Rules", 60, 5, 5, null), (defaults.Enabled, defaults.Provider, defaults.DebounceSeconds, defaults.MaxFailures, defaults.BatchSize / 4, defaults.RulesPath));

        var custom = ClassificationServiceCollectionExtensions.GetValidatedOptions(Config(("Classification:DebounceSeconds", "0"), ("Classification:Enabled", "false"), ("Classification:Provider", "rules")));
        Assert.Equal((false, 0, "Rules"), (custom.Enabled, custom.DebounceSeconds, custom.Provider));

        foreach (var (key, value) in new[]
                 {
                     ("Classification:Provider", "Llm"), ("Classification:DebounceSeconds", "-1"), ("Classification:MaxFailures", "0"), ("Classification:BatchSize", "0"),
                     ("Classification:PollIntervalMilliseconds", "1"), ("Classification:Enabled", "maybe"), ("Classification:RetryDelaySeconds", "many"),
                     ("Classification:ProcessingTimeoutSeconds", "0"),
                 })
        {
            var ex = Assert.Throws<InvalidOperationException>(() => ClassificationServiceCollectionExtensions.GetValidatedOptions(Config((key, value))));
            Assert.Contains(key, ex.Message);
        }
    }

    [Fact]
    public void ARulesFile_IsValidatedAtStartup_AndItsErrorNamesTheKeyButNotItsContent()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{ \"version\": 1, \"secret-looking-text\": true }");

            var ex = Assert.Throws<InvalidOperationException>(() => ClassificationServiceCollectionExtensions.GetValidatedOptions(Config(("Classification:RulesPath", path))));

            Assert.Contains("Classification:RulesPath", ex.Message);
            Assert.DoesNotContain("secret-looking-text", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }

        var missing = Assert.Throws<InvalidOperationException>(
            () => ClassificationServiceCollectionExtensions.GetValidatedOptions(Config(("Classification:RulesPath", Path.Combine(Path.GetTempPath(), "nope", "missing.json")))));
        Assert.Contains("Classification:RulesPath", missing.Message);
    }

    [Fact]
    public async Task ADeploymentCanPointTheClassifierAtItsOwnRules()
    {
        // A copy of the default rules where the word "axilas" is renamed to a code of another vertical: the host reads it from the file.
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
        try
        {
            var defaults = await File.ReadAllTextAsync(FindRulesFile());
            File.WriteAllText(path, defaults.Replace("\"code\": \"axilas\"", "\"code\": \"underarms\""));
            using var host = WebhookHost(_factory, new() { ["Classification:RulesPath"] = path });

            var result = await host.Services.GetRequiredService<IConversationClassifier>().ClassifyAsync(ChatScriptOf("Cuánto cuesta depilar axilas"));

            Assert.Equal("underarms", result.ServiceInterest);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static ConversationSnapshot ChatScriptOf(string customerText)
        => new(Guid.NewGuid(), null, DateTime.UtcNow, DateTime.UtcNow, null, [new SnapshotMessage(MessageDirection.Inbound, "text", customerText, DateTime.UtcNow)]);

    private static string FindRulesFile()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Conversia.slnx")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory!.FullName, "src", "Conversia.Application", "Classification", "Rules", "ClassificationRules.es-CO.json");
    }
}
