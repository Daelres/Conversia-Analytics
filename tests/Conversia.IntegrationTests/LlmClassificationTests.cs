using System.Collections.Concurrent;
using System.Data.Common;
using System.Text.Json.Nodes;
using Conversia.Application.Classification;
using Conversia.Application.Classification.Llm;
using Conversia.Application.Classification.Scheduling;
using Conversia.Domain.Entities;
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
/// The LLM provider on a real PostgreSQL: the rules read every conversation near real time, and the <see cref="LlmAnalysisRunner"/> reads it
/// on the company's schedule in three phases (design H1): a short claim with a lease, the call outside any lock, a short relock that applies
/// the answer only if nothing changed. The model is a fake <see cref="ILlmClient"/>: no test here calls a real API. Like
/// <see cref="ConversationClassificationTests"/>, each test lives in an era of its own far in the past.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public partial class LlmClassificationTests
{
    private static int _era;

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public LlmClassificationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // 1970 onwards, one month back per call: no other test class uses these years. 12:00 UTC = 07:00 Bogota.
    private static DateTime NewEra() => new DateTime(1975, 1, 1, 12, 0, 0, DateTimeKind.Utc).AddDays(-30 * Interlocked.Increment(ref _era));

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

    // ---- The fake model and the runner ---------------------------------------------------------------------------

    private static readonly LlmCatalog Catalog = LlmCatalog.FromRules(ClassificationRuleSet.LoadDefault());

    /// <summary>A fake model: answers with <see cref="Answer"/>, or runs <see cref="Hook"/> first (barriers, failures). Records every request.</summary>
    private sealed class FakeModel : ILlmClient
    {
        private int _calls;
        private int _inFlight;
        private int _maxInFlight;

        public Func<LlmRequest, string> Answer { get; set; } = _ => Json();

        public LlmStopReason Stop { get; set; } = LlmStopReason.EndTurn;

        public Func<CancellationToken, Task>? Hook { get; set; }

        public ConcurrentQueue<LlmRequest> Requests { get; } = new();

        public int Calls => Volatile.Read(ref _calls);

        public int MaxInFlight => Volatile.Read(ref _maxInFlight);

        public async Task<LlmCompletion> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            var now = Interlocked.Increment(ref _inFlight);
            int seen;
            while (now > (seen = Volatile.Read(ref _maxInFlight)) && Interlocked.CompareExchange(ref _maxInFlight, now, seen) != seen)
            {
            }

            try
            {
                Requests.Enqueue(request);
                if (Hook is { } hook)
                {
                    await hook(cancellationToken);
                }

                return new LlmCompletion(Stop, Answer(request), new LlmUsage(1200, 80, 2900, 0), "claude-haiku-5-5");
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
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
        Schedule = AnalysisScheduleSettings.Default with { SpreadMinutes = 0 },
    };

    private ClassificationCoordinator Coordinator(TimeProvider clock, ILlmClient model, ClassificationOptions options, IAiUsageMeter? meter = null, int? breakerFailures = null)
    {
        var rules = ClassificationRuleSet.LoadDefault();
        var llm = options.Llm!;
        var classifier = new LlmConversationClassifier(
            new ConversationPreprocessor(llm.Preprocessing, rules),
            new LlmPromptBuilder(new ExtractionSchema(Catalog, llm.ExtractAgeRange)),
            new LlmResultMapper(Catalog, llm.ExtractAgeRange),
            model,
            new LlmClassifierSettings(llm.Model, llm.ExtractAgeRange, llm.Preprocessing.Fingerprint, rules.Fingerprint(), llm.HashKey));
        return new ClassificationCoordinator(
            new RuleBasedConversationClassifier(rules), classifier, new LlmCircuitBreaker(breakerFailures ?? llm.BreakerFailures, TimeSpan.FromSeconds(llm.BreakerCooldownSeconds)),
            meter ?? NullAiUsageMeter.Instance,
            new LlmCoordinatorSettings(llm.MaxFallbackRetries, options.RetryDelay, llm.IncludeSandbox, LlmOptions.ProbeDeferral, LlmOptions.MaxJitterSeconds, llm.Pricing, llm.SandboxOnly),
            new Random(3), clock);
    }

    private LlmAnalysisRunner LlmRunner(
        TimeProvider clock, ILlmClient model, ILogger<LlmAnalysisRunner>? logger = null, ClassificationOptions? options = null, IAiUsageMeter? meter = null,
        ILlmBatchClient? batches = null, int? breakerFailures = null)
    {
        options ??= LlmRunnerOptions();
        return new LlmAnalysisRunner(
            _factory.Services.GetRequiredService<IServiceScopeFactory>(), options, clock, logger ?? NullLogger<LlmAnalysisRunner>.Instance,
            Coordinator(clock, model, options, meter, breakerFailures), batches);
    }

    /// <summary>The company's work at <paramref name="now"/> (no spread, so a nightly window opens exactly at its hour).</summary>
    private static TenantWork Work(Guid tenantId, DateTime now, AiAnalysisMode mode = AiAnalysisMode.NearRealTime, int? hour = null, int? interval = null)
        => new(tenantId, mode, AnalysisSchedule.WindowAt(new TenantAnalysisSchedule(mode, hour, interval), tenantId, now, LlmRunnerOptions().ScheduleSettings));

    private static async Task<int> DrainAsync(LlmAnalysisRunner runner, TenantWork work)
    {
        var handled = 0;
        while (await runner.ProcessNextAsync(work) != LlmStep.NothingDue)
        {
            handled++;
        }

        return handled;
    }

    private static async Task<Contact> LoadContactAsync(IServiceProvider services, Guid contactId)
    {
        using var scope = services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Contacts.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == contactId);
    }

    /// <summary>
    /// A conversation as ingestion leaves it with <c>requestClassification</c>, once the rules have read it: pending for the LLM, not flagged
    /// for the rules (the rules runner claims the oldest flagged conversation of any company, so these tests leave none flagged).
    /// </summary>
    private async Task<Seeded> SeedPendingAsync(
        ChannelFixture f, DateTime startedAt, IReadOnlyList<Msg> messages, string? waId = null, bool requestClassification = false, bool sandbox = false,
        bool aiOn = true)
    {
        var seeded = await SeedConversationAsync(_factory.Services, f, startedAt, messages, waId: waId, requestClassification: requestClassification, sandbox: sandbox);
        await ExecuteAsync($"UPDATE \"Conversations\" SET \"ClassificationDirty\" = false WHERE \"Id\" = {seeded.ConversationId}");
        if (aiOn)
        {
            // Phase 3 applies only for a company with the AI on (the scheduler only runs those).
            await ExecuteAsync($"UPDATE \"Tenants\" SET \"AiEnabled\" = true WHERE \"Id\" = {f.TenantId}");
        }
        return seeded;
    }

    private async Task ExecuteAsync(FormattableString sql)
    {
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.ExecuteSqlAsync(sql);
    }

    private async Task<AiAnalysisRun?> RunOfAsync(Guid tenantId, DateTime windowStart)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().AiAnalysisRuns.IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(r => r.TenantId == tenantId && r.WindowStart == windowStart);
    }

    /// <summary>A new message as ingestion records it: the request time changes, the conversation is pending again.</summary>
    private async Task RequestAgainAsync(Guid conversationId, DateTime at)
        => await ExecuteAsync($"""
            UPDATE "Conversations" SET "ClassificationRequestedAt" = {at}, "LlmPending" = true,
                "ClassificationFailures" = 0, "ClassificationAttemptedAt" = NULL, "LlmFailures" = 0 WHERE "Id" = {conversationId}
            """);

    private static readonly Msg[] PricedChat =
    [
        In(0, "Hola, soy mujer y quiero saber cuánto cuesta la depilación de axilas, mi cel 3001234567"),
        Out(60, "Son $60.000 la sesión."),
        In(120, "Muy caro, no gracias"),
    ];

    private string OtherService => Catalog.Services.First(s => s != "axilas");

    // ---- The reading, the profile, call reduction (as in the first stage, now outside the lock) ------------------------------

    [Fact]
    public async Task TheLlmReading_IsApplied_TheHashStored_AndTheProfileWrittenAfterTheCommit()
    {
        using var f = await StartChannelAsync(_factory, _client);
        var start = NewEra();
        var waId = UniqueWaId();
        var seeded = await SeedPendingAsync(f, start, PricedChat, waId: waId, requestClassification: true);
        var model = new FakeModel { Answer = _ => Json("lost", "price", "price", [OtherService], "female") };
        var capture = new LogCapture();
        using var loggers = LoggerFactory.Create(b => b.AddProvider(capture).SetMinimumLevel(LogLevel.Debug));
        var now = start.AddDays(1);
        var work = Work(f.TenantId, now);

        var handled = await DrainAsync(LlmRunner(new FixedTimeProvider(now), model, loggers.CreateLogger<LlmAnalysisRunner>()), work);

        Assert.Equal((1, 1), (handled, model.Calls));
        var (conversation, opportunity) = await LoadAsync(_factory.Services, seeded.ConversationId);
        Assert.Equal((false, 0, (DateTime?)null), (conversation.LlmPending, conversation.LlmFailures, conversation.LlmLeaseUntil));
        Assert.Equal((DateTime?)now, conversation.LlmAnalyzedAt);
        Assert.Equal(model.Requests.Single().SnapshotHash, conversation.LastLlmSnapshotHash);
        Assert.Equal((OpportunityStage.Lost, (LossReason?)LossReason.Price, ObjectionType.Price, OtherService),
            (opportunity.Stage, opportunity.LossReason, opportunity.Objection, opportunity.ServiceInterest));
        Assert.Equal(((ClassifierProvider?)ClassifierProvider.Llm, (ClassifierProvider?)ClassifierProvider.Llm), (opportunity.ServiceInterestClassifiedBy, opportunity.ObjectionClassifiedBy));
        Assert.StartsWith("llm.", opportunity.ClassificationRules);

        var contact = await LoadContactAsync(_factory.Services, seeded.ContactId);
        Assert.Equal((Gender.Female, (FieldSource?)FieldSource.Auto, (ClassifierProvider?)ClassifierProvider.Llm), (contact.Gender, contact.GenderSource, contact.ProfileClassifiedBy));

        var run = await RunOfAsync(f.TenantId, work.Window.Start);
        Assert.Equal((AiAnalysisMode.NearRealTime, 1, 1, 0), (run!.Mode, run.Claimed, run.Analysed, run.Discarded));

        // Pseudonymized before it left the process; the log line has ids, tokens, the phases and the preprocessor statistics, never text.
        var sent = model.Requests.Single().UserContent;
        Assert.DoesNotContain("3001234567", sent);
        Assert.DoesNotContain(waId, sent);
        var line = Assert.Single(capture.Entries, e => e.Message.StartsWith("LLM classification", StringComparison.Ordinal));
        foreach (var value in new[] { seeded.ConversationId.ToString(), f.TenantId.ToString(), "claude-haiku-5-5", "succeeded", "1200", "80", "2900", "analysed" })
        {
            Assert.True(line.Mentions(value), $"the log line should carry {value}: {line.FullText}");
        }

        foreach (var key in new[] { "CharactersBefore", "CharactersAfter", "TokensBefore", "TokensAfter", "Dropped", "Collapsed", "Truncated", "EstimatedUsd", "CacheReadTokens", "CacheWriteTokens", "Sandbox", "Mode", "Batch", "ClaimTransactionMs", "CallMs", "ApplyTransactionMs" })
        {
            Assert.Contains(line.Properties, p => p.Key == key);
        }

        Assert.DoesNotContain(capture.Entries, e => e.Mentions("caro") || e.Mentions("mujer") || e.Mentions("3001234567"));
    }

    [Fact]
    public async Task TheRulesPass_UnderTheLlmProvider_KeepsTheLlmFieldsAndTheStageCurrent_AndLeavesTheConversationPendingForTheLlm()
    {
        using var f = await StartChannelAsync(_factory, _client);
        var start = NewEra();
        var seeded = await SeedPendingAsync(f, start, PricedChat, requestClassification: true);
        var now = start.AddDays(1);
        await DrainAsync(LlmRunner(new FixedTimeProvider(now), new FakeModel { Answer = _ => Json("in_follow_up", services: [OtherService]) }), Work(f.TenantId, now));
        var hash = (await LoadAsync(_factory.Services, seeded.ConversationId)).Conversation.LastLlmSnapshotHash;

        // A new message: the rules read it near real time (fallback flag: the LLM's service stays), the LLM waits for its window.
        await RequestAgainAsync(seeded.ConversationId, now.AddMinutes(1));
        await ExecuteAsync($"UPDATE \"Conversations\" SET \"ClassificationDirty\" = true WHERE \"Id\" = {seeded.ConversationId}");
        var rules = new ConversationClassificationRunner(
            _factory.Services.GetRequiredService<IServiceScopeFactory>(), LlmRunnerOptions(), new FixedTimeProvider(now.AddMinutes(3)), NullLogger<ConversationClassificationRunner>.Instance);
        for (var i = 0; i < 100 && (await LoadAsync(_factory.Services, seeded.ConversationId)).Conversation.ClassificationDirty; i++)
        {
            Assert.True(await rules.ProcessNextAsync()); // the oldest flagged conversation of any company first
        }

        var (conversation, opportunity) = await LoadAsync(_factory.Services, seeded.ConversationId);
        Assert.Equal((false, true, hash), (conversation.ClassificationDirty, conversation.LlmPending, conversation.LastLlmSnapshotHash));
        Assert.Equal((OtherService, OpportunityStage.Lost), (opportunity.ServiceInterest, opportunity.Stage)); // the rules read the refusal at once
    }

    [Fact]
    public async Task WithTheMeteredMeter_ACompanyWithTheAiOff_IsSkipped_AndOnceSwitchedOn_EveryCallIsRecorded()
    {
        using var f = await StartChannelAsync(_factory, _client);
        var start = NewEra();
        var clock = new FixedTimeProvider(start.AddDays(1));
        var meter = new Infrastructure.Classification.Usage.AiUsageMeter(
            _factory.Services.GetRequiredService<IServiceScopeFactory>(), AiQuotaSettings.Default, clock, NullLogger<Infrastructure.Classification.Usage.AiUsageMeter>.Instance);
        var model = new FakeModel { Answer = _ => Json("lost", "price", "price", [OtherService]) };
        var seeded = await SeedPendingAsync(f, start, PricedChat, requestClassification: true, aiOn: false);

        await DrainAsync(LlmRunner(clock, model, meter: meter), Work(f.TenantId, clock.GetUtcNow().UtcDateTime));

        Assert.Equal(0, model.Calls); // the AI is off by default: nothing is sent
        // Not admitted is not "done": the conversation keeps waiting and is analysed once the company may be (review item 9).
        Assert.True((await LoadAsync(_factory.Services, seeded.ConversationId)).Conversation.LlmPending);

        await ExecuteAsync($"UPDATE \"Tenants\" SET \"AiEnabled\" = true WHERE \"Id\" = {f.TenantId}");
        clock.Advance(TimeSpan.FromHours(2)); // past the near-real-time retry of a conversation that was not admitted
        await DrainAsync(LlmRunner(clock, model, meter: meter), Work(f.TenantId, clock.GetUtcNow().UtcDateTime));

        Assert.Equal(1, model.Calls);
        using var scope = _factory.Services.CreateScope();
        var row = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().AiUsageMonths.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(u => u.TenantId == f.TenantId);
        Assert.Equal((AiQuotaSettings.Default.MonthOf(clock.GetUtcNow().UtcDateTime), false, 1, 1), (row.Month, row.IsSandbox, row.ConversationsAnalyzed, row.Calls));
        Assert.Equal((1200L, 80L, 2900L), (row.InputTokens, row.OutputTokens, row.CacheReadTokens));
        Assert.Equal(LlmPricing.Default.Estimate(new LlmUsage(1200, 80, 2900, 0)), row.EstimatedUsd);
    }

    [Fact]
    public async Task AContentFailureAfterAnLlmReading_KeepsTheLlmSourcedFields_AndIsCounted()
    {
        using var f = await StartChannelAsync(_factory, _client);
        var start = NewEra();
        var seeded = await SeedPendingAsync(f, start, PricedChat, requestClassification: true);
        var model = new FakeModel { Answer = _ => Json("in_follow_up", services: [OtherService]) };
        var clock = new FixedTimeProvider(start.AddDays(1));
        var runner = LlmRunner(clock, model);
        await DrainAsync(runner, Work(f.TenantId, clock.GetUtcNow().UtcDateTime));

        // Something asks for the conversation again (the stored hash is cleared so the model is asked), and the model now refuses.
        await ExecuteAsync($"UPDATE \"Conversations\" SET \"LlmPending\" = true, \"LastLlmSnapshotHash\" = NULL WHERE \"Id\" = {seeded.ConversationId}");
        model.Stop = LlmStopReason.Refusal;
        clock.Advance(TimeSpan.FromMinutes(2));
        await DrainAsync(runner, Work(f.TenantId, clock.GetUtcNow().UtcDateTime));

        var (conversation, opportunity) = await LoadAsync(_factory.Services, seeded.ConversationId);
        Assert.Equal((true, 1, (DateTime?)null), (conversation.LlmPending, conversation.LlmFailures, conversation.LlmLeaseUntil));
        Assert.Equal((OtherService, (ClassifierProvider?)ClassifierProvider.Llm), (opportunity.ServiceInterest, opportunity.ServiceInterestClassifiedBy));
        Assert.Equal(2, model.Calls);
    }

    [Fact]
    public async Task AnUnchangedSnapshot_IsFinishedWithoutASecondCall()
    {
        using var f = await StartChannelAsync(_factory, _client);
        var start = NewEra();
        var seeded = await SeedPendingAsync(f, start, PricedChat, requestClassification: true);
        var model = new FakeModel();
        var clock = new FixedTimeProvider(start.AddDays(1));
        var runner = LlmRunner(clock, model);
        await DrainAsync(runner, Work(f.TenantId, clock.GetUtcNow().UtcDateTime));
        var analysed = await LoadAsync(_factory.Services, seeded.ConversationId);

        await ExecuteAsync($"UPDATE \"Conversations\" SET \"LlmPending\" = true WHERE \"Id\" = {seeded.ConversationId}");
        clock.Advance(TimeSpan.FromMinutes(2));
        var handled = await DrainAsync(runner, Work(f.TenantId, clock.GetUtcNow().UtcDateTime));

        var conversation = (await LoadAsync(_factory.Services, seeded.ConversationId)).Conversation;
        Assert.Equal((1, 1, false), (handled, model.Calls, conversation.LlmPending));
        Assert.Equal((analysed.Conversation.LastLlmSnapshotHash, analysed.Conversation.LlmAnalyzedAt), (conversation.LastLlmSnapshotHash, conversation.LlmAnalyzedAt));
    }

    [Fact]
    public async Task AConversationWithoutCustomerText_IsSkipped_WithoutACall()
    {
        using var f = await StartChannelAsync(_factory, _client);
        var start = NewEra();
        var seeded = await SeedPendingAsync(f, start,
            [Out(0, "Hola, tenemos promoción de depilación de axilas este mes"), Out(60, "¿Te interesa agendar?")], requestClassification: true);
        var model = new FakeModel();
        var now = start.AddDays(1);

        var handled = await DrainAsync(LlmRunner(new FixedTimeProvider(now), model), Work(f.TenantId, now));

        var conversation = (await LoadAsync(_factory.Services, seeded.ConversationId)).Conversation;
        Assert.Equal((1, 0, false, (string?)null), (handled, model.Calls, conversation.LlmPending, conversation.LastLlmSnapshotHash));
        Assert.Equal(1, (await RunOfAsync(f.TenantId, Work(f.TenantId, now).Window.Start))!.Skipped);
    }

    [Fact]
    public async Task NearRealTime_WaitsForTheLlmDebounce_LikeTheFirstStage()
    {
        using var f = await StartChannelAsync(_factory, _client);
        var start = NewEra();
        await SeedPendingAsync(f, start, PricedChat, requestClassification: true);
        var model = new FakeModel();
        var lastMessage = start.AddSeconds(120);

        // 2 minutes after the last message: not past the LLM's 300 s; 6 minutes: read.
        var early = lastMessage.AddMinutes(2);
        Assert.Equal(0, await DrainAsync(LlmRunner(new FixedTimeProvider(early), model), Work(f.TenantId, early)));
        var later = lastMessage.AddMinutes(6);
        Assert.Equal(1, await DrainAsync(LlmRunner(new FixedTimeProvider(later), model), Work(f.TenantId, later)));
    }

    [Fact]
    public async Task ASandboxConversation_IsReadByTheLlm_AndItsLogLineSaysSandbox()
    {
        using var f = await StartChannelAsync(_factory, _client);
        var start = NewEra();
        var seeded = await SeedPendingAsync(f, start, PricedChat, requestClassification: true, sandbox: true);
        var model = new FakeModel();
        var capture = new LogCapture();
        using var loggers = LoggerFactory.Create(b => b.AddProvider(capture));
        var now = start.AddDays(1);

        await DrainAsync(LlmRunner(new FixedTimeProvider(now), model, loggers.CreateLogger<LlmAnalysisRunner>()), Work(f.TenantId, now));

        Assert.Equal(1, model.Calls);
        Assert.Equal(model.Requests.Single().SnapshotHash, (await LoadAsync(_factory.Services, seeded.ConversationId)).Conversation.LastLlmSnapshotHash);
        var line = Assert.Single(capture.Entries, e => e.Message.StartsWith("LLM classification", StringComparison.Ordinal));
        Assert.Contains(line.Properties, p => p.Key == "Sandbox" && p.Value == "True");
    }

    [Fact]
    public async Task AFailedProfileWrite_LeavesTheProfilePending_AndALaterRunWritesIt_WithoutCallingTheModelAgain()
    {
        using var f = await StartChannelAsync(_factory, _client);
        var start = NewEra();
        var seeded = await SeedPendingAsync(f, start, PricedChat, requestClassification: true);
        var model = new FakeModel { Answer = _ => Json(gender: "female") };
        var clock = new FixedTimeProvider(start.AddDays(1));
        var runner = LlmRunner(clock, model, options: LlmRunnerOptions(processingTimeoutSeconds: 2) with { RetryDelaySeconds = 60 });

        using (var holder = _factory.Services.CreateScope())
        {
            var db = holder.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await using var lockTx = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlAsync($"SELECT 1 FROM \"Contacts\" WHERE \"Id\" = {seeded.ContactId} FOR UPDATE");

            await DrainAsync(runner, Work(f.TenantId, clock.GetUtcNow().UtcDateTime));

            await lockTx.RollbackAsync();
        }

        var conversation = (await LoadAsync(_factory.Services, seeded.ConversationId)).Conversation;
        Assert.Equal((false, true, (Gender?)Gender.Female), (conversation.LlmPending, conversation.ProfilePending, conversation.PendingGender));
        Assert.Equal(Gender.Undetermined, (await LoadContactAsync(_factory.Services, seeded.ContactId)).Gender);

        await ExecuteAsync($"UPDATE \"Conversations\" SET \"ProfilePending\" = false WHERE \"ProfilePending\" AND \"TenantId\" <> {f.TenantId}"); // only ours is due
        Assert.False(await runner.WritePendingProfileAsync()); // within the retry delay: not yet
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.True(await runner.WritePendingProfileAsync());

        Assert.Equal(1, model.Calls);
        Assert.Equal(Gender.Female, (await LoadContactAsync(_factory.Services, seeded.ContactId)).Gender);
        Assert.False((await LoadAsync(_factory.Services, seeded.ConversationId)).Conversation.ProfilePending);
    }

    [Fact]
    public async Task ACrashBetweenTheCommitAndTheProfileWrite_IsRecoveredFromThePendingProfile()
    {
        using var f = await StartChannelAsync(_factory, _client);
        var start = NewEra();
        var seeded = await SeedPendingAsync(f, start, PricedChat);
        await ExecuteAsync($"UPDATE \"Conversations\" SET \"ProfilePending\" = false WHERE \"ProfilePending\""); // only ours is due
        await ExecuteAsync($"""
            UPDATE "Conversations" SET "ProfilePending" = true, "PendingAgeRange" = 'From25To34', "PendingGender" = 'Male',
                "LlmAttemptedAt" = {start.AddHours(1)} WHERE "Id" = {seeded.ConversationId}
            """);
        var model = new FakeModel();

        Assert.True(await LlmRunner(new FixedTimeProvider(start.AddDays(1)), model).WritePendingProfileAsync());

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
        var real = await SeedPendingAsync(f, start, PricedChat, requestClassification: true);
        var sandbox = await SeedPendingAsync(f, start.AddMinutes(1), PricedChat, requestClassification: true, sandbox: true);
        var model = new FakeModel();
        var capture = new LogCapture();
        using var loggers = LoggerFactory.Create(b => b.AddProvider(capture));
        var options = LlmRunnerOptions() with { Llm = LlmRunnerOptions().Llm! with { Client = LlmOptions.AnthropicClient } };
        var now = start.AddDays(1);

        await DrainAsync(LlmRunner(new FixedTimeProvider(now), model, loggers.CreateLogger<LlmAnalysisRunner>(), options), Work(f.TenantId, now));

        Assert.Equal(1, model.Calls);
        Assert.Null((await LoadAsync(_factory.Services, real.ConversationId)).Conversation.LastLlmSnapshotHash);
        Assert.NotNull((await LoadAsync(_factory.Services, sandbox.ConversationId)).Conversation.LastLlmSnapshotHash);
        var line = Assert.Single(capture.Entries, e => e.Level == LogLevel.Information && e.Mentions(ClassificationCoordinator.RealClientSandboxOnly));
        Assert.True(line.Mentions(real.ConversationId.ToString()));

        var later = await SeedPendingAsync(f, start.AddMinutes(2), PricedChat, requestClassification: true);
        var allowed = options with { Llm = options.Llm! with { AllowRealClientNonSandbox = true } };
        await DrainAsync(LlmRunner(new FixedTimeProvider(now), model, options: allowed), Work(f.TenantId, now));
        Assert.Equal(2, model.Calls);
        Assert.NotNull((await LoadAsync(_factory.Services, later.ConversationId)).Conversation.LastLlmSnapshotHash);
    }

    // ---- Three phases (design H1): no lock during the call, staleness, leases ---------------------------------------------------

    [Fact]
    public async Task DuringASlowCall_TheConversationRowIsFree_IngestionWritesItAtOnce_AndTheAnswerIsDiscarded()
    {
        // Session A plays ingestion of a new message while the model is still answering: it takes the Contact then the Conversation row
        // and rewrites the request. In the first stage it waited for the whole call; now it must not wait at all. Phase 3 then sees the
        // new request and throws the answer away: the conversation stays pending for the next window.
        using var f = await StartChannelAsync(_factory, _client);
        var start = NewEra();
        var seeded = await SeedPendingAsync(f, start, PricedChat, requestClassification: true);
        var now = start.AddDays(1);
        TimeSpan ingestionTook = TimeSpan.MaxValue;
        var model = new FakeModel
        {
            Answer = _ => Json("won", services: [OtherService]),
            Hook = async ct =>
            {
                using var ingestion = _factory.Services.CreateScope();
                var db = ingestion.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var watch = System.Diagnostics.Stopwatch.StartNew();
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                await db.Database.ExecuteSqlAsync($"SET LOCAL lock_timeout = '2s'", ct);
                await db.Database.ExecuteSqlAsync($"SELECT 1 FROM \"Contacts\" WHERE \"Id\" = {seeded.ContactId} FOR UPDATE", ct);
                await db.Database.ExecuteSqlAsync($"SELECT 1 FROM \"Conversations\" WHERE \"Id\" = {seeded.ConversationId} FOR UPDATE", ct);
                await db.Database.ExecuteSqlAsync(
                    $"UPDATE \"Conversations\" SET \"ClassificationRequestedAt\" = {now.AddSeconds(-1)}, \"LlmPending\" = true WHERE \"Id\" = {seeded.ConversationId}", ct);
                await tx.CommitAsync(ct);
                ingestionTook = watch.Elapsed;
            },
        };
        var before = await LoadAsync(_factory.Services, seeded.ConversationId);
        var work = Work(f.TenantId, now, AiAnalysisMode.Nightly, hour: 7);

        Assert.Equal(LlmStep.Called, await LlmRunner(new FixedTimeProvider(now), model).ProcessNextAsync(work));

        Assert.True(ingestionTook < TimeSpan.FromSeconds(1), $"ingestion waited {ingestionTook}");
        var (conversation, opportunity) = await LoadAsync(_factory.Services, seeded.ConversationId);
        Assert.Equal((true, (DateTime?)null, (DateTime?)now, (DateTime?)null), (conversation.LlmPending, conversation.LlmLeaseUntil, conversation.LlmAttemptedAt, conversation.LlmAnalyzedAt));
        Assert.Equal((before.Opportunity.Stage, before.Opportunity.ServiceInterest), (opportunity.Stage, opportunity.ServiceInterest));
        Assert.Equal(1, (await RunOfAsync(f.TenantId, work.Window.Start))!.Discarded);

        // One analysis per window: not again in this one, however many messages; the next night reads it.
        Assert.Equal(LlmStep.NothingDue, await LlmRunner(new FixedTimeProvider(now.AddMinutes(30)), model).ProcessNextAsync(work));
        var nextNight = now.AddDays(1);
        Assert.Equal(LlmStep.Called, await LlmRunner(new FixedTimeProvider(nextNight), model).ProcessNextAsync(Work(f.TenantId, nextNight, AiAnalysisMode.Nightly, hour: 7)));
        Assert.Equal(OtherService, (await LoadAsync(_factory.Services, seeded.ConversationId)).Opportunity.ServiceInterest);
    }

    [Fact]
    public async Task AStageChangedDuringTheCall_ByTheAutoCloserOrAPerson_DiscardsTheAnswer()
    {
        using var f = await StartChannelAsync(_factory, _client);
        var start = NewEra();
        var seeded = await SeedPendingAsync(f, start, PricedChat, requestClassification: true);
        var now = start.AddDays(1);
        var model = new FakeModel
        {
            Answer = _ => Json("in_follow_up", services: [OtherService]),
            // What ConversationAutoCloser writes on an idle conversation: the stage and its time (no new message).
            Hook = async ct => await ExecuteAsync($"""
                UPDATE "Opportunities" SET "Stage" = 'Lost', "LossReason" = 'NoResponse', "StageChangedAt" = {now}, "ClosedAt" = {now}
                WHERE "ConversationId" = {seeded.ConversationId}
                """),
        };

        await LlmRunner(new FixedTimeProvider(now), model).ProcessNextAsync(Work(f.TenantId, now));

        var (conversation, opportunity) = await LoadAsync(_factory.Services, seeded.ConversationId);
        Assert.Equal((true, OpportunityStage.Lost, (string?)null), (conversation.LlmPending, opportunity.Stage, opportunity.ServiceInterest == OtherService ? "applied" : null));
    }

    [Fact]
    public async Task WhatTheModelReadsChangedDuringTheCall_WithoutANewMessage_DiscardsTheAnswerByItsHash()
    {
        // An advisor assigned during the call changes the known names (an input of the snapshot hash and of the masking) but not the
        // request time or the stage: only the hash tells the answer is about a different snapshot.
        using var f = await StartChannelAsync(_factory, _client);
        var start = NewEra();
        var seeded = await SeedPendingAsync(f, start, PricedChat, requestClassification: true);
        var advisor = Advisor.Create(f.TenantId, f.Branch.Id, "Valentina Restrepo");
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.Advisors.Add(advisor);
            await db.SaveChangesAsync();
        }

        var now = start.AddDays(1);
        var model = new FakeModel
        {
            Answer = _ => Json("in_follow_up", services: [OtherService]),
            Hook = async _ => await ExecuteAsync($"UPDATE \"Conversations\" SET \"AdvisorId\" = {advisor.Id} WHERE \"Id\" = {seeded.ConversationId}"),
        };

        await LlmRunner(new FixedTimeProvider(now), model).ProcessNextAsync(Work(f.TenantId, now));

        var (conversation, opportunity) = await LoadAsync(_factory.Services, seeded.ConversationId);
        Assert.Equal((true, (string?)null), (conversation.LlmPending, conversation.LastLlmSnapshotHash));
        Assert.NotEqual(OtherService, opportunity.ServiceInterest);
    }

    [Fact]
    public async Task ALeaseKeepsAConversationFromBeingClaimedTwice_AndACrashedWorkersLeaseExpires()
    {
        using var f = await StartChannelAsync(_factory, _client);
        var start = NewEra();
        var seeded = await SeedPendingAsync(f, start, PricedChat, requestClassification: true);
        var now = start.AddDays(1);
        var work = Work(f.TenantId, now);
        LlmStep? second = null;
        var model = new FakeModel();
        model.Hook = async _ =>
        {
            model.Hook = null;
            second = await LlmRunner(new FixedTimeProvider(now), model).ProcessNextAsync(work); // another worker during the call
        };

        await LlmRunner(new FixedTimeProvider(now), model).ProcessNextAsync(work);
        Assert.Equal(LlmStep.NothingDue, second);
        Assert.Equal(1, model.Calls);

        // A worker that died after phase 1: the lease (the retry delay) is all that is left. Before it ends nobody claims the conversation;
        // after it, the next claim analyses it.
        await RequestAgainAsync(seeded.ConversationId, now);
        await ExecuteAsync($"UPDATE \"Conversations\" SET \"LlmLeaseUntil\" = {now.AddMinutes(10).AddSeconds(60)} WHERE \"Id\" = {seeded.ConversationId}");
        var later = now.AddMinutes(10);
        Assert.Equal(LlmStep.NothingDue, await LlmRunner(new FixedTimeProvider(later), model).ProcessNextAsync(Work(f.TenantId, later)));
        Assert.Equal(LlmStep.Handled, await LlmRunner(new FixedTimeProvider(later.AddSeconds(61)), model).ProcessNextAsync(Work(f.TenantId, later.AddSeconds(61))));
        Assert.False((await LoadAsync(_factory.Services, seeded.ConversationId)).Conversation.LlmPending);
    }

    [Fact]
    public async Task ARowStillLockedInPhase3_IsLeftToItsLease_AfterThreeTries()
    {
        using var f = await StartChannelAsync(_factory, _client);
        var start = NewEra();
        var seeded = await SeedPendingAsync(f, start, PricedChat, requestClassification: true);
        var now = start.AddDays(1);
        var capture = new LogCapture();
        using var loggers = LoggerFactory.Create(b => b.AddProvider(capture));
        using var holder = _factory.Services.CreateScope();
        var db = holder.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await using var lockTx = await db.Database.BeginTransactionAsync();
        var model = new FakeModel
        {
            Hook = async _ => await db.Database.ExecuteSqlAsync($"SELECT 1 FROM \"Conversations\" WHERE \"Id\" = {seeded.ConversationId} FOR UPDATE"),
        };

        await LlmRunner(new FixedTimeProvider(now), model, loggers.CreateLogger<LlmAnalysisRunner>()).ProcessNextAsync(Work(f.TenantId, now));
        await lockTx.RollbackAsync();

        var conversation = (await LoadAsync(_factory.Services, seeded.ConversationId)).Conversation;
        Assert.Equal((true, (DateTime?)now.AddSeconds(60)), (conversation.LlmPending, conversation.LlmLeaseUntil));
        Assert.Contains(capture.Entries, e => e.Mentions("stayed locked") && e.Mentions(seeded.ConversationId.ToString()));
    }

    // ---- Review round: ownership in phase 3, re-checks at apply, the rules-only path ------------------------------------------

    [Fact]
    public async Task AnotherWorkersLease_OrAReadingAppliedMeanwhile_MakesPhase3DiscardTheAnswer()
    {
        using var f = await StartChannelAsync(_factory, _client);
        var start = NewEra();
        var foreign = await SeedPendingAsync(f, start, PricedChat, requestClassification: true);
        var done = await SeedPendingAsync(f, start.AddMinutes(1), PricedChat, requestClassification: true);
        var now = start.AddDays(1);
        var model = new FakeModel { Answer = _ => Json("in_follow_up", services: [OtherService]) };
        // The lease expired during a slow call and another worker took the conversation (a lease of its own).
        model.Hook = async _ =>
        {
            var current = model.Requests.Count;
            var id = current == 1 ? foreign.ConversationId : done.ConversationId;
            if (current == 1)
            {
                await ExecuteAsync($"UPDATE \"Conversations\" SET \"LlmLeaseUntil\" = {now.AddMinutes(30)} WHERE \"Id\" = {id}");
            }
            else
            {
                await ExecuteAsync($"UPDATE \"Conversations\" SET \"LlmPending\" = false, \"LlmLeaseUntil\" = NULL WHERE \"Id\" = {id}");
            }
        };
        var runner = LlmRunner(new FixedTimeProvider(now), model);

        await runner.ProcessNextAsync(Work(f.TenantId, now));
        await runner.ProcessNextAsync(Work(f.TenantId, now));

        Assert.Equal(2, model.Calls);
        Assert.NotEqual(OtherService, (await LoadAsync(_factory.Services, foreign.ConversationId)).Opportunity.ServiceInterest);
        Assert.Equal((DateTime?)now.AddMinutes(30), (await LoadAsync(_factory.Services, foreign.ConversationId)).Conversation.LlmLeaseUntil); // the other worker's lease is kept
        Assert.NotEqual(OtherService, (await LoadAsync(_factory.Services, done.ConversationId)).Opportunity.ServiceInterest);
    }

    [Fact]
    public async Task TheAiSwitchedOffOrTheCompanyPausedDuringTheCall_DiscardsTheAnswer_AndTheConversationStaysPending()
    {
        using var f = await StartChannelAsync(_factory, _client);
        var start = NewEra();
        var seeded = await SeedPendingAsync(f, start, PricedChat, requestClassification: true);
        await ExecuteAsync($"UPDATE \"Tenants\" SET \"AiEnabled\" = true WHERE \"Id\" = {f.TenantId}");
        var now = start.AddDays(1);
        var model = new FakeModel
        {
            Answer = _ => Json("in_follow_up", services: [OtherService]),
            Hook = async _ => await ExecuteAsync($"UPDATE \"Tenants\" SET \"AiEnabled\" = false WHERE \"Id\" = {f.TenantId}"),
        };

        await LlmRunner(new FixedTimeProvider(now), model).ProcessNextAsync(Work(f.TenantId, now));

        var (conversation, opportunity) = await LoadAsync(_factory.Services, seeded.ConversationId);
        Assert.Equal((1, true, (DateTime?)null), (model.Calls, conversation.LlmPending, conversation.LlmLeaseUntil));
        Assert.NotEqual(OtherService, opportunity.ServiceInterest);
    }

    [Fact]
    public async Task UnderTheRulesProvider_NothingIsLeftPendingForAnLlmThatDoesNotRun()
    {
        using var f = await StartChannelAsync(_factory, _client);
        var start = NewEra();
        var seeded = await SeedConversationAsync(_factory.Services, f, start, PricedChat, requestClassification: true);
        var rules = new ConversationClassificationRunner(
            _factory.Services.GetRequiredService<IServiceScopeFactory>(), ClassificationOptions.Default, new FixedTimeProvider(start.AddDays(1)),
            NullLogger<ConversationClassificationRunner>.Instance);

        for (var i = 0; i < 100 && (await LoadAsync(_factory.Services, seeded.ConversationId)).Conversation.ClassificationDirty; i++)
        {
            Assert.True(await rules.ProcessNextAsync());
        }

        Assert.False((await LoadAsync(_factory.Services, seeded.ConversationId)).Conversation.LlmPending);
    }

    [Fact]
    public async Task TheHost_WiresTheScheduledRunnerOverReplay_WithoutAKey()
    {
        // Development, Provider=Llm, Client=Replay, no key: the host builds the scheduler, the runner and the Replay batch client.
        using var f = await StartChannelAsync(_factory, _client);
        var start = new DateTime(1950, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var seeded = await SeedPendingAsync(f, start, PricedChat, requestClassification: true);
        await ExecuteAsync($"UPDATE \"Tenants\" SET \"AiEnabled\" = true, \"AiAnalysisMode\" = 'NearRealTime' WHERE \"Id\" = {f.TenantId}");
        await using var host = WebhookHost(_factory, new()
        {
            ["Classification:Provider"] = "Llm",
            ["Classification:Llm:Client"] = "Replay",
            ["Classification:Llm:PrivacyApproved"] = "true",
        });

        var runner = host.Services.GetRequiredService<LlmAnalysisRunner>();
        Assert.NotEqual(LlmStep.NothingDue, await runner.ProcessNextAsync(Work(f.TenantId, TimeProvider.System.GetUtcNow().UtcDateTime))); // the host's clock

        var (conversation, opportunity) = await LoadAsync(_factory.Services, seeded.ConversationId);
        Assert.Matches("^[0-9a-f]{64}$", conversation.LastLlmSnapshotHash);
        Assert.StartsWith("llm.outcome.in_follow_up", opportunity.ClassificationRules);
        Assert.IsType<Infrastructure.Classification.Llm.ReplayLlmClient>(host.Services.GetRequiredService<ILlmClient>());
        Assert.IsType<Infrastructure.Classification.Llm.ReplayLlmBatchClient>(host.Services.GetRequiredService<ILlmBatchClient>());
        Assert.True(runner.BatchMode);
        Assert.NotNull(host.Services.GetRequiredService<LlmAnalysisScheduler>());
    }
}
