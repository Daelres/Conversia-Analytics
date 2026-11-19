using Conversia.Application.Assistant;
using Conversia.Application.Classification;
using Conversia.Application.Classification.Llm;
using Conversia.Application.Exceptions;
using Conversia.Application.Interfaces;
using Conversia.Application.Messaging;
using Conversia.Application.Time;
using Conversia.Domain.Common;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Domain.Interfaces;
using Conversia.Infrastructure.Classification;
using Conversia.Infrastructure.Persistence;
using Conversia.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Conversia.Infrastructure.Assistant;

/// <summary>
/// The AI sales assistant's job (docs/ASSISTANT.md, "Pipeline"), safe with several instances. Each run claims the due timers
/// (<c>FOR UPDATE SKIP LOCKED</c>, leased) and answers each one in three phases, like the LLM runner:
/// <list type="number">
/// <item><b>Prepare</b>, under the conversation lock: the timer is still the one claimed (same generation), every switch and fact is
/// re-checked (<see cref="AssistantPolicy"/>), the deterministic handoff triggers run (<see cref="AssistantRules"/>), and the pseudonymized
/// request is built. Discards and handoffs without a line end here.</item>
/// <item><b>Generate</b>, with no lock and no transaction: the typing indicator (when the company has read receipts on), then the
/// generator, bounded by a timeout.</item>
/// <item><b>Send</b>: a short locked re-check (a person may have answered meanwhile: the answer is dropped), then the send path with
/// <c>Sender = AiAssistant</c>, whose record step updates the state under the same lock as the message.</item>
/// </list>
/// </summary>
public sealed class AssistantRunner(
    IServiceScopeFactory scopes, AssistantOptions options, TenantTimeZone zone, ILogger<AssistantRunner> logger, TimeProvider? clock = null)
{
    public static readonly EventId AssistantJobFailed = new(4402, nameof(AssistantJobFailed));
    public static readonly EventId AssistantHandedOff = new(4403, nameof(AssistantHandedOff));
    public static readonly EventId AssistantReplied = new(4404, nameof(AssistantReplied));

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private TimeSpan Lease => TimeSpan.FromSeconds(options.LeaseSeconds);

    /// <summary>
    /// Claims and answers every timer due at <paramref name="utcNow"/>, up to <see cref="AssistantOptions.MaxParallel"/> at once. Each timer reads
    /// the clock again (as an offset from <paramref name="utcNow"/>, so a test's chosen time still holds). Returns how many were handled.
    /// </summary>
    public async Task<int> RunOnceAsync(DateTime utcNow, CancellationToken cancellationToken = default)
    {
        var started = _clock.GetTimestamp();
        DateTime Now() => utcNow + _clock.GetElapsedTime(started);
        var handled = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var claimed = await ClaimAsync(Now(), cancellationToken);
            await Parallel.ForEachAsync(claimed, new ParallelOptions { MaxDegreeOfParallelism = options.MaxParallel, CancellationToken = cancellationToken },
                async (timer, token) =>
                {
                    try
                    {
                        await HandleAsync(timer, Now(), token);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
                    {
                        // The claim expires and another run tries again (counted as an attempt); one conversation never stops the others.
                        logger.LogError(AssistantJobFailed, "The assistant timer of conversation {ConversationId} of tenant {TenantId} failed ({ExceptionType}).",
                            timer.ConversationId, timer.TenantId, ex.GetType().Name);
                    }

                    Interlocked.Increment(ref handled);
                });

            if (claimed.Count < options.BatchSize)
            {
                break;
            }
        }

        return handled;
    }

    /// <param name="Generation">The generation the claim produced: a later claim (lease lost) or any event moves it, and this holder stops.</param>
    public sealed record ClaimedTimer(Guid Id, Guid TenantId, Guid ConversationId, int Generation);

    /// <summary>The due timers, oldest first, leased to this instance. A timer another instance holds is skipped, never waited for.</summary>
    public async Task<IReadOnlyList<ClaimedTimer>> ClaimAsync(DateTime utcNow, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var now = DatabaseTime.Truncate(utcNow);
        var due = await db.ConversationAssistants
            .FromSql($"""
                SELECT * FROM "ConversationAssistants"
                WHERE "State" = 'Waiting' AND "DueAt" <= {now} AND ("ClaimedUntil" IS NULL OR "ClaimedUntil" <= {now})
                ORDER BY "DueAt" LIMIT {options.BatchSize} FOR UPDATE SKIP LOCKED
                """)
            .IgnoreQueryFilters()
            .ToListAsync(cancellationToken);
        foreach (var timer in due)
        {
            timer.TryClaim(now, Lease);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return [.. due.Select(t => new ClaimedTimer(t.Id, t.TenantId, t.ConversationId, t.Generation))];
    }

    private enum PlanKind
    {
        Done,
        Generate,
        Send,
    }

    /// <summary>What the prepare phase decided. <see cref="HandoffReason"/> with <see cref="Text"/> is a handoff line to send.</summary>
    private sealed record Plan(PlanKind Kind, AssistantReplyRequest? Request = null, string? Text = null, string? HandoffReason = null, bool Disclosed = false,
        AssistantKnowledge? Knowledge = null, string? LastInboundProviderId = null, bool Typing = false, string? HandoffLine = null, string? DisclosureText = null,
        bool Misunderstood = false);

    /// <summary>Answers one claimed timer (the run does this for each claim; tests call it to race two instances).</summary>
    public Task HandleClaimedAsync(ClaimedTimer timer, DateTime now, CancellationToken cancellationToken = default) => HandleAsync(timer, now, cancellationToken);

    private async Task HandleAsync(ClaimedTimer timer, DateTime now, CancellationToken cancellationToken)
    {
        // Phase 1: prepare, under the conversation lock.
        var plan = await InScopeAsync(timer, async (services, repository, conversations) =>
        {
            Plan result = new(PlanKind.Done);
            await conversations.ChangeSerializedWithIngestionAsync(timer.TenantId, timer.ConversationId, async conversation =>
            {
                var state = await repository.FindStateForUpdateAsync(timer.ConversationId, CancellationToken.None);
                if (state is null || !state.IsCurrent(timer.Generation))
                {
                    if (state is { State: AssistantState.Waiting })
                    {
                        state.ReleaseClaim(); // a newer customer message: claimed again at once, with it
                    }

                    return;
                }

                await CountAsync(repository, conversation, AssistantMetrics.TimerFired, now);
                result = await PrepareAsync(services, services.GetRequiredService<ApplicationDbContext>(), repository, conversation, state, now);
                AssistantSignals.Touch(conversation, now);
            }, cancellationToken);
            return result;
        });

        if (plan.Kind == PlanKind.Done)
        {
            return;
        }

        if (plan.Kind == PlanKind.Generate)
        {
            if (plan.Typing)
            {
                await InScopeAsync(timer, async (services, _, _) =>
                {
                    await ShowTypingAsync(services, services.GetRequiredService<ApplicationDbContext>(), timer, plan.LastInboundProviderId);
                    return true;
                });
            }

            plan = await InScopeAsync(timer, (services, _, _) => GenerateAsync(services, timer, plan, cancellationToken));
        }

        // Every later phase reads in a scope of its own: nothing tracked by the first phase can hide what changed meanwhile.
        await SendAsync(timer, plan, now);
    }

    private async Task<T> InScopeAsync<T>(ClaimedTimer timer, Func<IServiceProvider, IAssistantRepository, IConversationRepository, Task<T>> work)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(timer.TenantId);
        return await work(scope.ServiceProvider, scope.ServiceProvider.GetRequiredService<IAssistantRepository>(), scope.ServiceProvider.GetRequiredService<IConversationRepository>());
    }

    private sealed record Unanswered(IReadOnlyList<string> Texts, bool MediaWithoutText, string? LastProviderMessageId);

    private static async Task<Unanswered> UnansweredAsync(ApplicationDbContext db, Conversation conversation)
    {
        var rows = await db.Messages.AsNoTracking()
            .Where(m => m.ConversationId == conversation.Id && m.Direction == MessageDirection.Inbound
                        && (conversation.LastOutboundAt == null || m.SentAt > conversation.LastOutboundAt))
            .OrderByDescending(m => m.SentAt).ThenByDescending(m => m.CreatedAt)
            .Select(m => new { m.Text, m.Type, m.ProviderMessageId })
            .Take(10)
            .ToListAsync();
        return new Unanswered(
            [.. rows.Where(m => !string.IsNullOrWhiteSpace(m.Text)).Select(m => m.Text!).Reverse()],
            rows.Any(m => string.IsNullOrWhiteSpace(m.Text)),
            rows.FirstOrDefault()?.ProviderMessageId);
    }

    private async Task<Plan> PrepareAsync(
        IServiceProvider services, ApplicationDbContext db, IAssistantRepository repository, Conversation conversation, ConversationAssistant state, DateTime now)
    {
        // An earlier attempt started to send and never recorded it (crash, timeout): whether the customer got it is unknown, so never again.
        if (state.IsSendPending)
        {
            await HandOffAsync(repository, conversation, state, AssistantHandoffReasons.SendUnknown, now);
            return new Plan(PlanKind.Done);
        }

        if (state.Attempts > options.MaxAttempts)
        {
            await HandOffAsync(repository, conversation, state, AssistantHandoffReasons.TimerFailed, now);
            return new Plan(PlanKind.Done);
        }

        if (state.DueAt is { } due && now - due > TimeSpan.FromMinutes(options.LateCapMinutes))
        {
            await HandOffAsync(repository, conversation, state, AssistantHandoffReasons.TooLate, now);
            return new Plan(PlanKind.Done);
        }

        var settings = await repository.GetSettingsAsync(CancellationToken.None) ?? AssistantSettings.Create(conversation.TenantId);
        var published = await repository.GetPublishedKnowledgeAsync(CancellationToken.None);
        var knowledge = published is null ? null : AssistantKnowledgeJson.Deserialize(published.Content);
        var unanswered = await UnansweredAsync(db, conversation);
        var asksForHuman = unanswered.Texts.Any(AssistantRules.AsksForHuman);
        var facts = await FactsAsync(services, db, conversation, settings, knowledge, now);
        var decision = Decide(AssistantPolicy.Check(facts.Facts), asksForHuman);

        if (decision.Kind == AssistantDecisionKind.Discard)
        {
            state.Discard();
            await CountAsync(repository, conversation, AssistantMetrics.DiscardedBecause(decision.Reason!), now);
            return new Plan(PlanKind.Done);
        }

        // The customer is still waiting: a handoff tells them, unless the window is closed (only a template could reach them).
        if (decision.Kind == AssistantDecisionKind.HandOff)
        {
            if (decision.Reason == AssistantHandoffReasons.WindowClosed)
            {
                await HandOffAsync(repository, conversation, state, decision.Reason, now);
                return new Plan(PlanKind.Done);
            }

            return new Plan(PlanKind.Send, Text: settings.HandoffLine, HandoffReason: decision.Reason, Knowledge: knowledge);
        }

        // Hourly ceilings of assistant messages: the company's and the conversation's (a silent handoff: a person looks at it).
        var hourAgo = now.AddHours(-1);
        var tenantMessages = await db.Messages.AsNoTracking()
            .CountAsync(m => m.Sender == MessageSender.AiAssistant && m.SentAt >= hourAgo);
        var conversationMessages = await db.Messages.AsNoTracking()
            .CountAsync(m => m.ConversationId == conversation.Id && m.Sender == MessageSender.AiAssistant && m.SentAt >= hourAgo);
        if (tenantMessages >= options.TenantMessagesPerHour || conversationMessages >= options.ConversationMessagesPerHour)
        {
            await HandOffAsync(repository, conversation, state, AssistantHandoffReasons.RateLimited, now);
            return new Plan(PlanKind.Done);
        }

        // An instruction aimed at the assistant anywhere in what the generator would read, not only in the unanswered messages.
        var context = await db.Messages.AsNoTracking()
            .Where(m => m.ConversationId == conversation.Id && m.Direction == MessageDirection.Inbound && m.Text != null)
            .OrderByDescending(m => m.SentAt).Take(options.ContextMessages).Select(m => m.Text!).ToListAsync();
        var reason = context.Any(AssistantRules.LooksLikeInjection) ? AssistantHandoffReasons.InjectionSuspected
            : AssistantRules.HandoffReason(unanswered.Texts, settings, state);
        if (reason is null && unanswered.MediaWithoutText)
        {
            reason = AssistantHandoffReasons.MediaNotUnderstood; // audio or a file without text: no generator here reads it
        }

        if (reason is not null)
        {
            return new Plan(PlanKind.Send, Text: settings.HandoffLine, HandoffReason: reason, Knowledge: knowledge);
        }

        // Asked whether it is a bot, in any unanswered message: the truth, in the company's words, whatever the disclosure mode
        // (docs/ASSISTANT.md, "Disclosure"). It is a reply, so it counts as a turn.
        if (unanswered.Texts.Any(AssistantRules.AsksIfAssistant))
        {
            return new Plan(PlanKind.Send, Text: settings.DisclosureText, Disclosed: true, Knowledge: knowledge);
        }

        var mustDisclose = settings.DisclosureMode == AssistantDisclosureMode.FirstMessage && state.DisclosedAt is null;
        var snapshot = Pseudonymizer.Pseudonymize(await ConversationClassificationRunner.ReadSnapshotAsync(db, conversation, includeKnownValues: true, CancellationToken.None));
        snapshot = snapshot with { Messages = snapshot.Messages.TakeLast(options.ContextMessages).ToList() };
        var request = new AssistantReplyRequest(
            conversation.TenantId, conversation.Id, conversation.IsSandbox, snapshot, published?.Id, published?.Number,
            new AssistantReplySettings(settings.Language, settings.ToneNotes, settings.DisclosureMode, settings.DisclosureText, state.AssistantTurns, settings.MaxAssistantTurns),
            mustDisclose);
        return new Plan(PlanKind.Generate, request, Knowledge: knowledge, LastInboundProviderId: unanswered.LastProviderMessageId,
            Typing: facts.ReadReceipts, HandoffLine: settings.HandoffLine, DisclosureText: mustDisclose ? settings.DisclosureText : null);
    }

    // A customer asking for a person is told someone will help, even outside the active hours (a handoff line is never "an answer").
    private static AssistantDecision Decide(AssistantDecision decision, bool asksForHuman)
        => asksForHuman && decision is { Kind: AssistantDecisionKind.Discard, Reason: AssistantDiscardReasons.OutsideHours }
            ? AssistantDecision.HandOff(AssistantHandoffReasons.HumanRequested)
            : decision;

    private sealed record FactsRead(AssistantFacts Facts, bool ReadReceipts);

    private async Task<FactsRead> FactsAsync(
        IServiceProvider services, ApplicationDbContext db, Conversation conversation, AssistantSettings settings, AssistantKnowledge? knowledge, DateTime now)
    {
        var tenant = await db.Tenants.AsNoTracking().Where(t => t.Id == conversation.TenantId)
            .Select(t => new { t.Status, t.AiEnabled, t.AssistantBlocked, t.SandboxMode, t.ReadReceiptsEnabled })
            .FirstAsync();
        var channel = await db.WhatsAppChannels.AsNoTracking().Where(c => c.Id == conversation.ChannelId)
            .Select(c => new { c.IsActive, c.AssistantEnabled })
            .FirstOrDefaultAsync();
        var global = await services.GetRequiredService<IPlatformSwitchStore>().IsEnabledAsync(PlatformSwitch.Assistant);
        var admitted = await services.GetRequiredService<IAssistantAdmission>().AdmitAsync(conversation.TenantId, conversation.IsSandbox);
        var facts = new AssistantFacts(
            GlobalEnabled: global,
            CompanyBlocked: tenant.AssistantBlocked,
            CompanyActive: tenant.Status == TenantStatus.Active,
            AiEnabled: tenant.AiEnabled,
            AssistantEnabled: settings.Enabled,
            ChannelActive: channel?.IsActive == true,
            ChannelAssistantEnabled: channel?.AssistantEnabled == true,
            SandboxMatches: conversation.IsSandbox == tenant.SandboxMode,
            ConversationOpen: conversation.Status == ConversationStatus.Open,
            AwaitingReply: conversation.AwaitingReply,
            WindowOpen: CustomerServiceWindow.IsOpen(conversation.LastInboundAt, now),
            WithinActiveHours: AssistantHours.IsActive(settings, knowledge, conversation.BranchId, now, zone.Zone),
            Admitted: admitted);
        return new FactsRead(facts, tenant.ReadReceiptsEnabled);
    }

    // Best effort and bounded: the customer sees "escribiendo..." while the answer is prepared. A failure is logged with ids only.
    private async Task ShowTypingAsync(IServiceProvider services, ApplicationDbContext db, ClaimedTimer timer, string? providerMessageId)
    {
        if (providerMessageId is null)
        {
            return;
        }

        try
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(options.TypingTimeoutSeconds));
            var conversation = await db.Conversations.AsNoTracking().FirstAsync(c => c.Id == timer.ConversationId, budget.Token);
            var channel = await db.WhatsAppChannels.AsNoTracking().FirstAsync(c => c.Id == conversation.ChannelId, budget.Token);
            var sandbox = await db.Tenants.AsNoTracking().Where(t => t.Id == timer.TenantId).Select(t => t.SandboxMode).FirstAsync(budget.Token);
            var sender = services.GetRequiredService<IWhatsAppMessageSenderResolver>().Resolve(channel, sandbox);
            await sender.MarkReadAsync(new WhatsAppReadReceipt(channel, providerMessageId, Typing: true), budget.Token);
        }
        catch (Exception ex)
        {
            logger.LogWarning("The typing indicator of conversation {ConversationId} could not be shown ({ExceptionType}).", timer.ConversationId, ex.GetType().Name);
        }
    }

    private async Task<Plan> GenerateAsync(IServiceProvider services, ClaimedTimer timer, Plan plan, CancellationToken cancellationToken)
    {
        var request = plan.Request!;
        AssistantReplyResult result;
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(TimeSpan.FromSeconds(options.GenerationTimeoutSeconds));
            var generator = services.GetRequiredService<IAssistantReplyGeneratorResolver>().Resolve(request.IsSandbox);
            result = await generator.GenerateAsync(request, budget.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("The reply generator failed for conversation {ConversationId} ({ExceptionType}).", timer.ConversationId, ex.GetType().Name);
            return plan with { Kind = PlanKind.Send, Text = plan.HandoffLine, HandoffReason = AssistantHandoffReasons.GeneratorFailed };
        }

        if (result.Usage is { } usage)
        {
            await services.GetRequiredService<IAssistantAdmission>().RecordUsageAsync(timer.TenantId, timer.ConversationId, request.IsSandbox, usage, CancellationToken.None);
        }

        switch (result)
        {
            case AssistantReplyResult.Reply reply:
                var text = plan.DisclosureText is { } disclosure ? disclosure + "\n\n" + reply.Text : reply.Text;
                return AssistantRules.CheckReply(text, plan.Knowledge) is { } checkedText
                    ? plan with { Kind = PlanKind.Send, Text = checkedText, Disclosed = plan.DisclosureText is not null }
                    : plan with { Kind = PlanKind.Send, Text = plan.HandoffLine, HandoffReason = AssistantHandoffReasons.GeneratorFailed };
            case AssistantReplyResult.Handoff handoff:
                var reason = KnownReason(handoff.Reason);
                var line = handoff.CustomerLine is null ? null
                    : handoff.CustomerLine.Length == 0 ? plan.HandoffLine
                    : AssistantRules.CheckReply(handoff.CustomerLine, plan.Knowledge) ?? plan.HandoffLine;
                return plan with { Kind = PlanKind.Send, Text = line, HandoffReason = reason };
            case AssistantReplyResult.NoReply noReply:
                // Nothing to send now; a misunderstanding counts towards the handoff (decided under the lock, in the send phase).
                return plan with { Kind = PlanKind.Send, Text = null, HandoffReason = null, Misunderstood = noReply.Reason == AssistantReplyResult.NotUnderstood };
            default:
                return plan with { Kind = PlanKind.Send, Text = plan.HandoffLine, HandoffReason = AssistantHandoffReasons.GeneratorFailed };
        }
    }

    // A generator's reason that is not one of the known codes becomes "the generator decided".
    private static string KnownReason(string? reason)
        => reason is not null && AssistantHandoffReasons.All.Contains(reason) ? reason : AssistantHandoffReasons.GeneratorHandoff;

    private async Task SendAsync(ClaimedTimer timer, Plan plan, DateTime now)
    {
        // Phase 3a: is the answer still wanted? A person (or a newer customer message, or a later claim) may have come in while the generator
        // ran, and every switch is read again. When it is, the send is marked as started and committed before the provider is called.
        var wanted = await InScopeAsync(timer, async (services, repository, conversations) =>
        {
            var go = false;
            await conversations.ChangeSerializedWithIngestionAsync(timer.TenantId, timer.ConversationId, async conversation =>
            {
                var state = await repository.FindStateForUpdateAsync(timer.ConversationId, CancellationToken.None);
                if (state is null || !state.IsCurrent(timer.Generation))
                {
                    if (state is { State: AssistantState.Waiting })
                    {
                        state.ReleaseClaim();
                    }

                    await CountAsync(repository, conversation, AssistantMetrics.Superseded, now);
                    return;
                }

                var db = services.GetRequiredService<ApplicationDbContext>();
                var settings = await repository.GetSettingsAsync(CancellationToken.None) ?? AssistantSettings.Create(conversation.TenantId);
                var published = await repository.GetPublishedKnowledgeAsync(CancellationToken.None);
                var knowledge = published is null ? null : AssistantKnowledgeJson.Deserialize(published.Content);
                var facts = await FactsAsync(services, db, conversation, settings, knowledge, now);
                var decision = AssistantPolicy.Check(facts.Facts);
                if (decision.Kind == AssistantDecisionKind.Discard && !(plan.HandoffReason is not null && decision.Reason == AssistantDiscardReasons.OutsideHours))
                {
                    state.Discard();
                    await CountAsync(repository, conversation, AssistantMetrics.DiscardedBecause(decision.Reason!), now);
                    return;
                }

                if (decision.Kind == AssistantDecisionKind.HandOff)
                {
                    if (decision.Reason == AssistantHandoffReasons.WindowClosed)
                    {
                        await HandOffAsync(repository, conversation, state, decision.Reason, now); // nobody to tell it to
                        return;
                    }

                    // The customer is still reachable: whatever was to be said becomes the handoff line (a prepared reply is not sent).
                    if (plan.HandoffReason is null)
                    {
                        plan = plan with { Text = settings.HandoffLine, HandoffReason = decision.Reason, Misunderstood = false };
                    }
                }

                // A generator that did not understand: counted, and the conversation goes to a person once it happens too often.
                if (plan.Misunderstood)
                {
                    state.RecordMisunderstanding();
                    if (state.MisunderstandingCount < AssistantSettings.MisunderstandingLimit)
                    {
                        state.Discard();
                        return;
                    }

                    plan = plan with { Text = plan.HandoffLine, HandoffReason = AssistantHandoffReasons.Misunderstanding };
                }

                if (plan.Text is null)
                {
                    // Nothing to tell the customer: a silent handoff, or nothing at all.
                    if (plan.HandoffReason is { } silent)
                    {
                        await HandOffAsync(repository, conversation, state, silent, now);
                    }
                    else
                    {
                        state.Discard();
                    }

                    return;
                }

                state.BeginSend(now, AssistantSignals.TextHash(plan.Text));
                go = true;
            }, CancellationToken.None);
            return go;
        });

        if (!wanted)
        {
            return;
        }

        // Phase 3b: the send path (provider call outside any lock, bounded); its record step updates the state with the message.
        try
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(options.SendTimeoutSeconds));
            await InScopeAsync(timer, (services, repository, _) => services.GetRequiredService<IMessageSendingService>().SendAsAssistantAsync(timer.ConversationId, plan.Text!, async conversation =>
            {
                var state = await repository.FindStateForUpdateAsync(timer.ConversationId, CancellationToken.None);
                if (state is null || !state.IsCurrent(timer.Generation))
                {
                    // A person or the customer wrote during the provider call: the message stands, the state stays theirs.
                    if (state is { State: AssistantState.Waiting })
                    {
                        state.ReleaseClaim();
                    }

                    await CountAsync(repository, conversation, AssistantMetrics.Superseded, now);
                    return;
                }

                if (plan.HandoffReason is { } reason)
                {
                    await HandOffAsync(repository, conversation, state, reason, now);
                    await CountAsync(repository, conversation, AssistantMetrics.HandoffLineSent, now);
                }
                else
                {
                    state.RecordReply(now, plan.Disclosed);
                    await CountAsync(repository, conversation, AssistantMetrics.Replied, now);
                    logger.LogInformation(AssistantReplied, "The assistant replied in conversation {ConversationId} of tenant {TenantId} (turn {Turn}).",
                        conversation.Id, conversation.TenantId, state.AssistantTurns);
                }

                AssistantSignals.Touch(conversation, now);
            }, budget.Token));
        }
        catch (Exception ex) when (ex is MessagingException or OperationCanceledException)
        {
            // Not sent, or sent and not recorded, or no answer in time: a person looks at it; a timeout's outcome is unknown. Ids and codes only.
            var code = ex is MessagingException messaging ? messaging.Code : "timeout";
            logger.LogWarning("The assistant could not send in conversation {ConversationId} of tenant {TenantId}: {Code}.", timer.ConversationId, timer.TenantId, code);
            var reason = code switch
            {
                "customer_service_window_closed" => AssistantHandoffReasons.WindowClosed,
                "timeout" or "provider_unavailable" or "message_sent_not_recorded" => AssistantHandoffReasons.SendUnknown,
                _ => AssistantHandoffReasons.SendFailed,
            };
            await InScopeAsync(timer, async (_, repository, conversations) => await conversations.ChangeSerializedWithIngestionAsync(timer.TenantId, timer.ConversationId, async conversation =>
            {
                var state = await repository.FindStateForUpdateAsync(timer.ConversationId, CancellationToken.None);
                if (state is not null && state.IsCurrent(timer.Generation))
                {
                    await HandOffAsync(repository, conversation, state, reason, now);
                    await CountAsync(repository, conversation, AssistantMetrics.SendFailed, now);
                }
            }, CancellationToken.None));
        }
    }

    private async Task HandOffAsync(IAssistantRepository repository, Conversation conversation, ConversationAssistant state, string reason, DateTime now)
    {
        state.HandOff(reason, now);
        AssistantSignals.Touch(conversation, now);
        await CountAsync(repository, conversation, AssistantMetrics.Handoff(reason), now);
        logger.LogInformation(AssistantHandedOff, "The assistant handed conversation {ConversationId} of tenant {TenantId} to a person: {Reason}.",
            conversation.Id, conversation.TenantId, reason);
    }

    private Task CountAsync(IAssistantRepository repository, Conversation conversation, string metric, DateTime now)
        => repository.CountAsync(conversation.TenantId, conversation.IsSandbox, metric, zone.LocalDate(now));

    /// <summary>Deletes the counters older than <see cref="AssistantOptions.CountersRetentionDays"/>. Returns the rows deleted.</summary>
    public async Task<int> PurgeCountersAsync(DateTime utcNow, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var before = zone.LocalDate(utcNow).AddDays(-options.CountersRetentionDays);
        return await db.AssistantActivityCounts.IgnoreQueryFilters().Where(c => c.Day < before).ExecuteDeleteAsync(cancellationToken);
    }
}

/// <summary><c>Assistant:JobEnabled</c> drives this: off in the test hosts (they call the runner with the time they choose).</summary>
public sealed class AssistantJobService(AssistantRunner runner, AssistantOptions options, TimeProvider time, ILogger<AssistantJobService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastPurge = DateTime.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = time.GetUtcNow().UtcDateTime;
            try
            {
                await runner.RunOnceAsync(now, stoppingToken);
                if (now - lastPurge > TimeSpan.FromHours(6))
                {
                    await runner.PurgeCountersAsync(now, stoppingToken);
                    lastPurge = now;
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(AssistantRunner.AssistantJobFailed, "The assistant job failed ({ExceptionType}); it runs again in {Seconds} seconds.", ex.GetType().Name, options.PollSeconds);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(options.PollSeconds), time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
