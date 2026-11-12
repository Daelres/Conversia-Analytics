using System.Collections.Concurrent;
using System.Data.Common;
using System.Text.Json.Nodes;
using Conversia.Application.Classification;
using Conversia.Application.Classification.Llm;
using Conversia.Domain.Enums;
using Conversia.Infrastructure.Classification;
using Conversia.Infrastructure.Configuration;
using Conversia.Infrastructure.Persistence;
using Conversia.Tests.Shared;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>
/// The LLM provider on a real PostgreSQL (design PR 2b-2, Stage A): the runner reads the snapshot with the known values, the coordinator
/// decides, the result (or the rules fallback) is applied in the claim transaction and the contact profile is written after the commit in a
/// transaction of its own. The model is a fake <see cref="Application.Classification.Llm.ILlmClient"/>: no test here calls a real API. Like
/// <see cref="ConversationClassificationTests"/>, each test lives in an era of its own far in the past.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class LlmClassificationTests
{
    private static int _era;

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public LlmClassificationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // 1970 onwards, one month back per call: no other test class uses these years.
    private static DateTime NewEra() => new DateTime(1975, 1, 1, 12, 0, 0, DateTimeKind.Utc).AddDays(-30 * Interlocked.Increment(ref _era));

    private static async Task<int> DrainAsync(ConversationClassificationRunner runner)
    {
        var handled = 0;
        while (await runner.ProcessNextAsync())
        {
            handled++;
        }

        return handled;
    }

    private sealed class CommandCounter : DbCommandInterceptor
    {
        private int _commands;

        public int Commands => Volatile.Read(ref _commands);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _commands);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _commands);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _commands);
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    // ---- Provider=Rules: exactly as before -----------------------------------------------------------------------

    [Fact]
    public async Task TheRulesPath_IssuesExactlyTheCommandsItIssuedBefore()
    {
        // Characterization (measured before the LLM wiring): the claim, the two timeouts, the messages, the branch, the opportunity and
        // one save. Provider=Rules must not read the known values, compute a hash or write anything else.
        var counter = new CommandCounter();
        await using var host = _factory.WithWebHostBuilder(b => b.ConfigureServices(s => s.ConfigureDbContext<ApplicationDbContext>(o => o.AddInterceptors(counter))));
        using var f = await StartChannelAsync(_factory, _client);
        var start = NewEra();
        var seeded = await SeedConversationAsync(_factory.Services, f, start, [In(0, "Hola, cuánto cuesta axilas?"), Out(60, "Son $60.000."), In(120, "No gracias")], requestClassification: true);

        var runner = new ConversationClassificationRunner(
            host.Services.GetRequiredService<IServiceScopeFactory>(), ClassificationOptions.Default with { RetryDelaySeconds = 0 },
            new FixedTimeProvider(start.AddDays(1)), NullLogger<ConversationClassificationRunner>.Instance);
        var before = counter.Commands;
        Assert.True(await runner.ProcessNextAsync());
        var used = counter.Commands - before;

        Assert.False((await LoadAsync(_factory.Services, seeded.ConversationId)).Conversation.ClassificationDirty);
        Assert.Equal(RulesPathCommands, used);
    }

    /// <summary>Measured on the runner as it was before the LLM wiring (HEAD 3571cf8).</summary>
    private const int RulesPathCommands = 7;

    // ---- Provider=Llm (Stage A, fake client) ---------------------------------------------------------------------

    private static readonly LlmCatalog Catalog = LlmCatalog.FromRules(ClassificationRuleSet.LoadDefault());

    /// <summary>A fake model: answers with <see cref="Answer"/>, or runs <see cref="Hook"/> first (barriers, failures). Records every request.</summary>
    private sealed class FakeModel : ILlmClient
    {
        private int _calls;

        public Func<LlmRequest, string> Answer { get; set; } = _ => Json();

        public LlmStopReason Stop { get; set; } = LlmStopReason.EndTurn;

        public Func<CancellationToken, Task>? Hook { get; set; }

        public ConcurrentQueue<LlmRequest> Requests { get; } = new();

        public int Calls => Volatile.Read(ref _calls);

        public async Task<LlmCompletion> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            Requests.Enqueue(request);
            if (Hook is { } hook)
            {
                await hook(cancellationToken);
            }

            return new LlmCompletion(Stop, Answer(request), new LlmUsage(1200, 80, 2900, 0), "claude-haiku-5-5");
        }
    }

    private static string Json(string stage = "in_follow_up", string? loss = null, string objection = "none", string[]? services = null, string gender = "undetermined")
        => new JsonObject
        {
            ["stage"] = stage,
            ["loss_reason"] = loss,
            ["objection"] = objection,
            ["services"] = new JsonArray([.. (services ?? []).Select(s => (JsonNode?)JsonValue.Create(s))]),
            ["interest_reason"] = null,
            ["gender"] = gender,
            ["confidence"] = 0.9,
            ["won_evidence_line"] = null,
        }.ToJsonString();

    private static ClassificationOptions LlmRunnerOptions(int processingTimeoutSeconds = 30) => ClassificationOptions.Default with
    {
        Provider = ClassificationOptions.LlmProvider,
        RetryDelaySeconds = 60,
        ProcessingTimeoutSeconds = processingTimeoutSeconds,
        Llm = LlmOptions.Default(development: true) with { Client = LlmOptions.ReplayClient, PrivacyApproved = true, DebounceSeconds = 300 },
    };

    private ConversationClassificationRunner LlmRunner(TimeProvider clock, ILlmClient model, ILogger<ConversationClassificationRunner>? logger = null, ClassificationOptions? options = null)
    {
        options ??= LlmRunnerOptions();
        var rules = ClassificationRuleSet.LoadDefault();
        var llm = options.Llm!;
        var classifier = new LlmConversationClassifier(
            new ConversationPreprocessor(llm.Preprocessing, rules),
            new LlmPromptBuilder(new ExtractionSchema(Catalog, llm.ExtractAgeRange)),
            new LlmResultMapper(Catalog, llm.ExtractAgeRange),
            model,
            new LlmClassifierSettings(llm.Model, llm.ExtractAgeRange, llm.Preprocessing.Fingerprint, rules.Fingerprint(), llm.HashKey));
        var coordinator = new ClassificationCoordinator(
            new RuleBasedConversationClassifier(rules), classifier, new LlmCircuitBreaker(llm.BreakerFailures, TimeSpan.FromSeconds(llm.BreakerCooldownSeconds)),
            NullAiUsageMeter.Instance,
            new LlmCoordinatorSettings(llm.MaxFallbackRetries, options.RetryDelay, llm.IncludeSandbox, LlmOptions.ProbeDeferral, LlmOptions.MaxJitterSeconds, llm.Pricing, llm.SandboxOnly),
            new Random(3), clock);
        return new ConversationClassificationRunner(
            _factory.Services.GetRequiredService<IServiceScopeFactory>(), options, clock, logger ?? NullLogger<ConversationClassificationRunner>.Instance, coordinator);
    }

    private static async Task<Domain.Entities.Contact> LoadContactAsync(IServiceProvider services, Guid contactId)
    {
        using var scope = services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Contacts.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == contactId);
    }

    private async Task ExecuteAsync(FormattableString sql)
    {
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.ExecuteSqlAsync(sql);
    }

    private static readonly Msg[] PricedChat =
    [
        In(0, "Hola, soy mujer y quiero saber cuánto cuesta la depilación de axilas, mi cel 3001234567"),
        Out(60, "Son $60.000 la sesión."),
        In(120, "Muy caro, no gracias"),
    ];

    private string OtherService => Catalog.Services.First(s => s != "axilas");

    [Fact]
    public async Task TheLlmReading_IsApplied_TheHashStored_AndTheProfileWrittenAfterTheCommit()
    {
        using var f = await StartChannelAsync(_factory, _client);
        var start = NewEra();
        var waId = UniqueWaId();
        var seeded = await SeedConversationAsync(_factory.Services, f, start, PricedChat, waId: waId, requestClassification: true);
        var model = new FakeModel { Answer = _ => Json("lost", "price", "price", [OtherService], "female") };
        var capture = new LogCapture();
        using var loggers = LoggerFactory.Create(b => b.AddProvider(capture).SetMinimumLevel(LogLevel.Debug));

        var handled = await DrainAsync(LlmRunner(new FixedTimeProvider(start.AddDays(1)), model, loggers.CreateLogger<ConversationClassificationRunner>()));

        Assert.Equal((1, 1), (handled, model.Calls));
        var (conversation, opportunity) = await LoadAsync(_factory.Services, seeded.ConversationId);
        Assert.Equal((false, 0), (conversation.ClassificationDirty, conversation.LlmFailures));
        Assert.Equal(model.Requests.Single().SnapshotHash, conversation.LastLlmSnapshotHash);
        Assert.Equal((OpportunityStage.Lost, (LossReason?)LossReason.Price, ObjectionType.Price, OtherService),
            (opportunity.Stage, opportunity.LossReason, opportunity.Objection, opportunity.ServiceInterest));
        Assert.Equal(((ClassifierProvider?)ClassifierProvider.Llm, (ClassifierProvider?)ClassifierProvider.Llm), (opportunity.ServiceInterestClassifiedBy, opportunity.ObjectionClassifiedBy));
        Assert.StartsWith("llm.", opportunity.ClassificationRules);

        var contact = await LoadContactAsync(_factory.Services, seeded.ContactId);
        Assert.Equal((Gender.Female, (FieldSource?)FieldSource.Auto, (ClassifierProvider?)ClassifierProvider.Llm), (contact.Gender, contact.GenderSource, contact.ProfileClassifiedBy));

        // Pseudonymized before it left the process; the log line has ids, tokens and the preprocessor statistics, never text.
        var sent = model.Requests.Single().UserContent;
        Assert.DoesNotContain("3001234567", sent);
        Assert.DoesNotContain(waId, sent);
        var line = Assert.Single(capture.Entries, e => e.Message.StartsWith("LLM classification", StringComparison.Ordinal));
        foreach (var value in new[] { seeded.ConversationId.ToString(), f.TenantId.ToString(), "claude-haiku-5-5", "succeeded", "1200", "80", "2900" })
        {
            Assert.True(line.Mentions(value), $"the log line should carry {value}: {line.FullText}");
        }

        foreach (var key in new[] { "CharactersBefore", "CharactersAfter", "TokensBefore", "TokensAfter", "Dropped", "Collapsed", "Truncated", "EstimatedUsd", "CacheReadTokens", "CacheWriteTokens", "Sandbox" })
        {
            Assert.Contains(line.Properties, p => p.Key == key);
        }

        Assert.DoesNotContain(capture.Entries, e => e.Mentions("caro") || e.Mentions("mujer") || e.Mentions("3001234567"));
    }

    [Fact]
    public async Task AFallbackAfterAnLlmReading_KeepsTheLlmSourcedFields_AndCountsTheContentFailure()
    {
        using var f = await StartChannelAsync(_factory, _client);
        var start = NewEra();
        var seeded = await SeedConversationAsync(_factory.Services, f, start, PricedChat, requestClassification: true);
        var model = new FakeModel { Answer = _ => Json("in_follow_up", services: [OtherService]) };
        var clock = new FixedTimeProvider(start.AddDays(1));
        var runner = LlmRunner(clock, model);
        await DrainAsync(runner);
        Assert.Equal(OtherService, (await LoadAsync(_factory.Services, seeded.ConversationId)).Opportunity.ServiceInterest);

        // Something asks for the conversation again (the stored hash is cleared so the model is asked), and the model now refuses.
        await ExecuteAsync($"UPDATE \"Conversations\" SET \"ClassificationDirty\" = true, \"LastLlmSnapshotHash\" = NULL WHERE \"Id\" = {seeded.ConversationId}");
        model.Stop = LlmStopReason.Refusal;
        clock.Advance(TimeSpan.FromMinutes(2));
        await DrainAsync(runner);

        var (conversation, opportunity) = await LoadAsync(_factory.Services, seeded.ConversationId);
        Assert.Equal((true, 1, 0, (string?)null), (conversation.ClassificationDirty, conversation.LlmFailures, conversation.ClassificationFailures, conversation.LastLlmSnapshotHash));
        Assert.Equal((OtherService, (ClassifierProvider?)ClassifierProvider.Llm), (opportunity.ServiceInterest, opportunity.ServiceInterestClassifiedBy));
        Assert.Equal(OpportunityStage.Lost, opportunity.Stage); // stage, loss reason and confidence come from the rules (accepted mix, CC-7 S9)
        Assert.Equal(2, model.Calls);

        // Leave nothing flagged: the runners of other test classes claim the oldest flagged conversation across all tenants.
        await ExecuteAsync($"UPDATE \"Conversations\" SET \"ClassificationDirty\" = false WHERE \"Id\" = {seeded.ConversationId}");
    }

    [Fact]
    public async Task AnUnchangedSnapshot_IsFinishedWithoutASecondCall()
    {
        using var f = await StartChannelAsync(_factory, _client);
        var start = NewEra();
        var seeded = await SeedConversationAsync(_factory.Services, f, start, PricedChat, requestClassification: true);
        var model = new FakeModel();
        var clock = new FixedTimeProvider(start.AddDays(1));
        var runner = LlmRunner(clock, model);
        await DrainAsync(runner);
        var hash = (await LoadAsync(_factory.Services, seeded.ConversationId)).Conversation.LastLlmSnapshotHash;

        await ExecuteAsync($"UPDATE \"Conversations\" SET \"ClassificationDirty\" = true WHERE \"Id\" = {seeded.ConversationId}");
        clock.Advance(TimeSpan.FromMinutes(2));
        var handled = await DrainAsync(runner);

        var conversation = (await LoadAsync(_factory.Services, seeded.ConversationId)).Conversation;
        Assert.Equal((1, 1, false, hash), (handled, model.Calls, conversation.ClassificationDirty, conversation.LastLlmSnapshotHash));
    }

    [Fact]
    public async Task AConversationWithoutCustomerText_IsReadByTheRules_WithoutACall()
    {
        using var f = await StartChannelAsync(_factory, _client);
        var start = NewEra();
        var seeded = await SeedConversationAsync(_factory.Services, f, start,
            [Out(0, "Hola, tenemos promoción de depilación de axilas este mes"), Out(60, "¿Te interesa agendar?")], requestClassification: true);
        var model = new FakeModel();

        var handled = await DrainAsync(LlmRunner(new FixedTimeProvider(start.AddDays(1)), model));

        var (conversation, opportunity) = await LoadAsync(_factory.Services, seeded.ConversationId);
        Assert.Equal((1, 0, false, 0, (string?)null), (handled, model.Calls, conversation.ClassificationDirty, conversation.LlmFailures, conversation.LastLlmSnapshotHash));
        Assert.Equal((ClassifierProvider?)ClassifierProvider.Rules, opportunity.ServiceInterestClassifiedBy);
    }

    [Fact]
    public async Task TheLlmPath_WaitsForItsOwnLongerDebounce()
    {
        using var f = await StartChannelAsync(_factory, _client);
        var start = NewEra();
        var seeded = await SeedConversationAsync(_factory.Services, f, start, PricedChat, requestClassification: true);
        var model = new FakeModel();
        var lastMessage = start.AddSeconds(120);

        // 2 minutes after the last message: past the rules' 60 s, not past the LLM's 300 s.
        Assert.Equal(0, await DrainAsync(LlmRunner(new FixedTimeProvider(lastMessage.AddMinutes(2)), model)));
        Assert.Equal(1, await DrainAsync(LlmRunner(new FixedTimeProvider(lastMessage.AddMinutes(6)), model)));
        Assert.False((await LoadAsync(_factory.Services, seeded.ConversationId)).Conversation.ClassificationDirty);
    }

    [Fact]
    public async Task ASandboxConversation_IsReadByTheLlm_AndItsLogLineSaysSandbox()
    {
        using var f = await StartChannelAsync(_factory, _client);
        var start = NewEra();
        var seeded = await SeedConversationAsync(_factory.Services, f, start, PricedChat, requestClassification: true, sandbox: true);
        var model = new FakeModel();
        var capture = new LogCapture();
        using var loggers = LoggerFactory.Create(b => b.AddProvider(capture));

        await DrainAsync(LlmRunner(new FixedTimeProvider(start.AddDays(1)), model, loggers.CreateLogger<ConversationClassificationRunner>()));

        Assert.Equal(1, model.Calls);
        Assert.Equal(model.Requests.Single().SnapshotHash, (await LoadAsync(_factory.Services, seeded.ConversationId)).Conversation.LastLlmSnapshotHash);
        var line = Assert.Single(capture.Entries, e => e.Message.StartsWith("LLM classification", StringComparison.Ordinal));
        Assert.Contains(line.Properties, p => p.Key == "Sandbox" && p.Value == "True");
    }

    [Fact]
    public async Task AFailedProfileWrite_LeavesTheProfilePending_AndALaterRunWritesIt_WithoutCallingTheModelAgain()
    {
        // CP-4 S4 (hardened): the Contact row is held by another session longer than the profile transaction may wait. The reading is already
        // committed, with the profile pending on the conversation: the worker writes it later from the stored values, no second call.
        using var f = await StartChannelAsync(_factory, _client);
        var start = NewEra();
        var seeded = await SeedConversationAsync(_factory.Services, f, start, PricedChat, requestClassification: true);
        var model = new FakeModel { Answer = _ => Json(gender: "female") };
        var clock = new FixedTimeProvider(start.AddDays(1));
        var runner = LlmRunner(clock, model, options: LlmRunnerOptions(processingTimeoutSeconds: 2) with { RetryDelaySeconds = 60 });

        using (var holder = _factory.Services.CreateScope())
        {
            var db = holder.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await using var lockTx = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlAsync($"SELECT 1 FROM \"Contacts\" WHERE \"Id\" = {seeded.ContactId} FOR UPDATE");

            await DrainAsync(runner);

            await lockTx.RollbackAsync();
        }

        var conversation = (await LoadAsync(_factory.Services, seeded.ConversationId)).Conversation;
        Assert.Equal((false, true, (Gender?)Gender.Female), (conversation.ClassificationDirty, conversation.ProfilePending, conversation.PendingGender));
        Assert.NotNull(conversation.LastLlmSnapshotHash);
        Assert.Equal(Gender.Undetermined, (await LoadContactAsync(_factory.Services, seeded.ContactId)).Gender);

        Assert.Equal(0, await DrainAsync(runner)); // within the retry delay: not yet
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(1, await DrainAsync(runner));

        Assert.Equal(1, model.Calls);
        Assert.Equal(Gender.Female, (await LoadContactAsync(_factory.Services, seeded.ContactId)).Gender);
        var after = (await LoadAsync(_factory.Services, seeded.ConversationId)).Conversation;
        Assert.Equal((false, false, (Gender?)null), (after.ClassificationDirty, after.ProfilePending, after.PendingGender));
    }

    [Fact]
    public async Task ACrashBetweenTheCommitAndTheProfileWrite_IsRecoveredFromThePendingProfile()
    {
        // The state a crash right after the classification commit leaves: classified, not flagged, the profile pending.
        using var f = await StartChannelAsync(_factory, _client);
        var start = NewEra();
        var seeded = await SeedConversationAsync(_factory.Services, f, start, PricedChat);
        await ExecuteAsync($"""
            UPDATE "Conversations" SET "ProfilePending" = true, "PendingAgeRange" = 'From25To34', "PendingGender" = 'Male',
                "ClassificationAttemptedAt" = {start.AddHours(1)} WHERE "Id" = {seeded.ConversationId}
            """);
        var model = new FakeModel();

        Assert.Equal(1, await DrainAsync(LlmRunner(new FixedTimeProvider(start.AddDays(1)), model)));

        Assert.Equal(0, model.Calls);
        var contact = await LoadContactAsync(_factory.Services, seeded.ContactId);
        Assert.Equal(((AgeRange?)AgeRange.From25To34, Gender.Male, (ClassifierProvider?)ClassifierProvider.Llm), (contact.AgeRange, contact.Gender, contact.ProfileClassifiedBy));
        Assert.False((await LoadAsync(_factory.Services, seeded.ConversationId)).Conversation.ProfilePending);
    }

    [Fact]
    public async Task WithTheRealClient_OnlySandboxConversationsReachIt_UntilAllowed()
    {
        using var f = await StartChannelAsync(_factory, _client);
        var start = NewEra();
        var real = await SeedConversationAsync(_factory.Services, f, start, PricedChat, requestClassification: true);
        var sandbox = await SeedConversationAsync(_factory.Services, f, start.AddMinutes(1), PricedChat, requestClassification: true, sandbox: true);
        var model = new FakeModel();
        var capture = new LogCapture();
        using var loggers = LoggerFactory.Create(b => b.AddProvider(capture));
        var options = LlmRunnerOptions() with { Llm = LlmRunnerOptions().Llm! with { Client = LlmOptions.AnthropicClient } };

        await DrainAsync(LlmRunner(new FixedTimeProvider(start.AddDays(1)), model, loggers.CreateLogger<ConversationClassificationRunner>(), options));

        Assert.Equal(1, model.Calls);
        Assert.Equal((string?)null, (await LoadAsync(_factory.Services, real.ConversationId)).Conversation.LastLlmSnapshotHash);
        Assert.NotNull((await LoadAsync(_factory.Services, sandbox.ConversationId)).Conversation.LastLlmSnapshotHash);
        var line = Assert.Single(capture.Entries, e => e.Level == LogLevel.Information && e.Mentions(ClassificationCoordinator.RealClientSandboxOnly));
        Assert.True(line.Mentions(real.ConversationId.ToString()));

        // Allowed explicitly: a new real conversation reaches the client.
        var later = await SeedConversationAsync(_factory.Services, f, start.AddMinutes(2), PricedChat, requestClassification: true);
        var allowed = options with { Llm = options.Llm! with { AllowRealClientNonSandbox = true } };
        await DrainAsync(LlmRunner(new FixedTimeProvider(start.AddDays(1)), model, options: allowed));
        Assert.Equal(2, model.Calls);
        Assert.NotNull((await LoadAsync(_factory.Services, later.ConversationId)).Conversation.LastLlmSnapshotHash);
    }

    [Fact]
    public async Task ALongClaimTransaction_IsLoggedWithItsDuration_AndWarnedAboveTheThreshold()
    {
        using var f = await StartChannelAsync(_factory, _client);
        var start = NewEra();
        var seeded = await SeedConversationAsync(_factory.Services, f, start, PricedChat, requestClassification: true);
        var model = new FakeModel { Hook = ct => Task.Delay(300, ct) };
        var capture = new LogCapture();
        using var loggers = LoggerFactory.Create(b => b.AddProvider(capture));
        var options = LlmRunnerOptions() with { Llm = LlmRunnerOptions().Llm! with { ClaimWarningMilliseconds = 100 } };

        await DrainAsync(LlmRunner(new FixedTimeProvider(start.AddDays(1)), model, loggers.CreateLogger<ConversationClassificationRunner>(), options));

        var call = Assert.Single(capture.Entries, e => e.Message.StartsWith("LLM classification", StringComparison.Ordinal));
        Assert.Contains(call.Properties, p => p.Key == "ClaimTransactionMs" && int.Parse(p.Value) >= 300);
        var warning = Assert.Single(capture.Entries, e => e.Level == LogLevel.Warning && e.Mentions("ClaimTransactionMs"));
        Assert.True(warning.Mentions(seeded.ConversationId.ToString()));
    }

    [Fact]
    public async Task TheHost_WiresTheLlmProviderOverReplay_WithoutAKey()
    {
        // CC-11 S1: Development, Provider=Llm, Client=Replay, no key: the service starts and the runner it builds reads with the LLM path.
        // The conversation lives in 1950, older than anything else, so the single claim below takes it whatever other tests left flagged.
        using var f = await StartChannelAsync(_factory, _client);
        var start = new DateTime(1950, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var seeded = await SeedConversationAsync(_factory.Services, f, start, PricedChat, requestClassification: true);
        await using var host = WebhookHost(_factory, new()
        {
            ["Classification:Provider"] = "Llm",
            ["Classification:Llm:Client"] = "Replay",
            ["Classification:Llm:PrivacyApproved"] = "true",
        });

        Assert.True(await host.Services.GetRequiredService<ConversationClassificationRunner>().ProcessNextAsync());

        var (conversation, opportunity) = await LoadAsync(_factory.Services, seeded.ConversationId);
        Assert.Matches("^[0-9a-f]{64}$", conversation.LastLlmSnapshotHash);
        Assert.StartsWith("llm.outcome.in_follow_up", opportunity.ClassificationRules);
        Assert.IsType<Infrastructure.Classification.Llm.ReplayLlmClient>(host.Services.GetRequiredService<ILlmClient>());
    }

    [Fact]
    public async Task IngestionAndTheRunner_TakeContactAndConversationInAnOrderThatCannotDeadlock()
    {
        // Design D5, spec CP-6 S2. Session A plays ingestion of a contact's message: it holds the Contact row, then asks for the Conversation
        // row. Session B is the real runner: it holds the Conversation row during the (fake) call, commits, and only THEN writes the Contact
        // profile in a transaction of its own. Barriers make each wait until the other holds its first lock. A runner that wrote the Contact
        // while holding the Conversation would deadlock here (PostgreSQL would abort one side with 40P01).
        using var f = await StartChannelAsync(_factory, _client);
        var start = NewEra();
        var seeded = await SeedConversationAsync(_factory.Services, f, start, PricedChat, requestClassification: true);
        var runnerHoldsConversation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ingestionHoldsContact = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new FakeModel
        {
            Answer = _ => Json(gender: "female"),
            Hook = async ct =>
            {
                runnerHoldsConversation.TrySetResult();
                await ingestionHoldsContact.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
            },
        };
        var runner = LlmRunner(new FixedTimeProvider(start.AddDays(1)), model);

        var runnerTask = Task.Run(() => runner.ProcessNextAsync());

        await runnerHoldsConversation.Task.WaitAsync(TimeSpan.FromSeconds(20));
        using var ingestion = _factory.Services.CreateScope();
        var db = ingestion.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            await db.Database.ExecuteSqlAsync($"SELECT 1 FROM \"Contacts\" WHERE \"Id\" = {seeded.ContactId} FOR UPDATE");
            var ingestionPid = await db.Database.SqlQuery<int>($"SELECT pg_backend_pid() AS \"Value\"").SingleAsync();
            ingestionHoldsContact.SetResult();

            // Waits for the runner's commit, then holds both rows like ingestion does.
            await db.Database.ExecuteSqlAsync($"SELECT 1 FROM \"Conversations\" WHERE \"Id\" = {seeded.ConversationId} FOR UPDATE");

            // The runner is now waiting for this session's Contact lock (its profile write), and this session waits for nobody: no cycle.
            using var observer = _factory.Services.CreateScope();
            var watch = observer.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var deadline = DateTime.UtcNow.AddSeconds(20);
            int waiting;
            do
            {
                waiting = await watch.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pg_stat_activity WHERE {ingestionPid} = ANY(pg_blocking_pids(pid))").SingleAsync();
                if (waiting == 0)
                {
                    await Task.Delay(50);
                }
            }
            while (waiting == 0 && DateTime.UtcNow < deadline);

            Assert.Equal(1, waiting);
            Assert.Equal(0, await watch.Database.SqlQuery<int>($"SELECT cardinality(pg_blocking_pids({ingestionPid}))::int AS \"Value\"").SingleAsync());

            await db.Database.ExecuteSqlAsync($"UPDATE \"Conversations\" SET \"LastMessageAt\" = \"LastMessageAt\" WHERE \"Id\" = {seeded.ConversationId}");
            await tx.CommitAsync();
        }

        Assert.True(await runnerTask.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Equal(Gender.Female, (await LoadContactAsync(_factory.Services, seeded.ContactId)).Gender);
        Assert.False((await LoadAsync(_factory.Services, seeded.ConversationId)).Conversation.ClassificationDirty);
    }
}
