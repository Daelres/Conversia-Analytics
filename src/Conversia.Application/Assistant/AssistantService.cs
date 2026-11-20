using Conversia.Application.DTOs.Assistant;
using Conversia.Application.Exceptions;
using Conversia.Application.Time;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace Conversia.Application.Assistant;

/// <summary><c>Assistant:*</c>: the terms owners accept and the job's pace (docs/ASSISTANT.md, "Configuration").</summary>
public sealed record AssistantOptions
{
    public const string DefaultTermsVersion = "asistente-2026-11";

    /// <summary>
    /// What the assistant says when it did not understand (the first and second time in a row; the third goes to a person). Natural and
    /// varied: each strike uses the next line (<c>Assistant:ClarificationLines</c>).
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultClarificationLines =
    [
        "Perdona, no te entendí bien. ¿Me cuentas un poco más qué estás buscando?",
        "Disculpa, se me escapó lo que quisiste decir. ¿Me lo escribes de otra forma?",
        "Creo que no te entendí. ¿Qué servicio te interesa o qué te gustaría saber?",
    ];

    public IReadOnlyList<string> ClarificationLines { get; init; } = DefaultClarificationLines;

    /// <summary>The version of the assistant terms an owner must accept to switch it on.</summary>
    public string TermsVersion { get; init; } = DefaultTermsVersion;

    /// <summary>The job runs in this process (off in the test hosts, which call the runner themselves).</summary>
    public bool JobEnabled { get; init; } = true;

    /// <summary>How often the job looks for due timers (the latency target is about five seconds).</summary>
    public int PollSeconds { get; init; } = 2;

    /// <summary>How long a claimed timer is held by the instance answering it.</summary>
    public int LeaseSeconds { get; init; } = 120;

    public int BatchSize { get; init; } = 20;

    /// <summary>The generator gets at most this long; a slower answer is a handoff (<c>generator_failed</c>).</summary>
    public int GenerationTimeoutSeconds { get; init; } = 45;

    /// <summary>How many of the latest messages the generator reads.</summary>
    public int ContextMessages { get; init; } = 30;

    /// <summary>Days the activity counters are kept.</summary>
    public int CountersRetentionDays { get; init; } = 90;

    /// <summary>A timer that fires more than this many minutes after its due time is handed to a person (<c>too_late</c>).</summary>
    public int LateCapMinutes { get; init; } = 10;

    /// <summary>A timer claimed more than this many times is handed to a person (<c>timer_failed</c>).</summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>Timers answered at once by one instance: one slow call never holds the others.</summary>
    public int MaxParallel { get; init; } = 8;

    /// <summary>The provider call of a send gets at most this long; past it the outcome is unknown and it is never repeated.</summary>
    public int SendTimeoutSeconds { get; init; } = 30;

    public int TypingTimeoutSeconds { get; init; } = 5;

    /// <summary>When the customer asks for a person, the timer fires within this many seconds.</summary>
    public int HumanRequestDelaySeconds { get; init; } = 5;

    /// <summary>Assistant messages a company may send in an hour (all its conversations).</summary>
    public int TenantMessagesPerHour { get; init; } = 300;

    /// <summary>Assistant messages one conversation may get in an hour.</summary>
    public int ConversationMessagesPerHour { get; init; } = 8;

    /// <summary>How long after the assistant started a send an echo with the same text is taken as its own.</summary>
    public TimeSpan OwnEchoWindow => TimeSpan.FromSeconds(LeaseSeconds);
}

/// <summary>A platform switch (docs/PLATFORM_ADMIN.md, "Assistant"), read on every fired timer and changed by the administrator with an audit row.</summary>
public interface IPlatformSwitchStore
{
    Task<bool> IsEnabledAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Returns false, and writes nothing, when it already was that way.</summary>
    Task<bool> SetAsync(string name, bool enabled, Guid adminId, string reason, CancellationToken cancellationToken = default);
}

public interface IAssistantService
{
    Task<AssistantSettingsResponse> GetSettingsAsync(Guid tenantId, CancellationToken cancellationToken = default);

    Task<AssistantSettingsResponse> UpdateSettingsAsync(Guid tenantId, UpdateAssistantSettingsRequest request, Guid userId, CancellationToken cancellationToken = default);

    Task<AssistantSettingsResponse> EnableAsync(Guid tenantId, EnableAssistantRequest request, Guid ownerUserId, CancellationToken cancellationToken = default);

    Task<AssistantSettingsResponse> DisableAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken = default);

    Task<AssistantSettingsResponse> SetChannelAsync(Guid tenantId, Guid channelId, bool enabled, Guid userId, CancellationToken cancellationToken = default);

    Task<KnowledgeListResponse> ListKnowledgeAsync(CancellationToken cancellationToken = default);

    Task<KnowledgeVersionResponse> GetKnowledgeAsync(int number, CancellationToken cancellationToken = default);

    Task<KnowledgeVersionResponse> SaveKnowledgeAsync(Guid tenantId, AssistantKnowledge content, Guid userId, CancellationToken cancellationToken = default);

    Task<KnowledgeVersionResponse> PublishKnowledgeAsync(Guid tenantId, int number, Guid userId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AssistantAuditEntryResponse>> ListAuditAsync(CancellationToken cancellationToken = default);

    Task<ConversationAssistantResponse> GetConversationAsync(Guid conversationId, CancellationToken cancellationToken = default);

    Task<ConversationAssistantResponse> TakeOverAsync(Guid tenantId, Guid conversationId, Guid userId, CancellationToken cancellationToken = default);

    Task<ConversationAssistantResponse> HandBackAsync(Guid tenantId, Guid conversationId, Guid userId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The company's use cases of the AI sales assistant. Its trail is the structured log (event 4401 <see cref="AssistantTrail"/>: ids, the
/// action and the new values, never a text of the knowledge base or of a customer), as for the other company settings; the knowledge base
/// versions are their own trail (who saved and who published each one).
/// </summary>
public sealed class AssistantService(
    IAssistantRepository assistant, IWhatsAppChannelRepository channels, IBranchRepository branches, ITenantRepository tenants,
    IConversationRepository conversations, IPlatformSwitchStore switches, AssistantOptions options, TimeProvider time, TenantTimeZone zone,
    ILogger<AssistantService> logger) : IAssistantService
{
    public static readonly EventId AssistantTrail = new(4401, nameof(AssistantTrail));

    public const int KnowledgeListSize = 50;

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public async Task<AssistantSettingsResponse> GetSettingsAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var settings = await assistant.GetSettingsAsync(cancellationToken) ?? AssistantSettings.Create(tenantId);
        var tenant = await tenants.GetByIdAsync(tenantId, cancellationToken);
        var lines = await channels.ListAsync(cancellationToken);
        var published = await assistant.GetPublishedKnowledgeAsync(cancellationToken);
        return new AssistantSettingsResponse
        {
            Enabled = settings.Enabled,
            TermsVersion = settings.TermsVersion,
            TermsAcceptedAt = settings.TermsAcceptedAt,
            TermsAcceptedBy = settings.TermsAcceptedBy,
            CurrentTermsVersion = options.TermsVersion,
            ReplyDelaySeconds = settings.ReplyDelaySeconds,
            ActiveHours = settings.ActiveHours.ToString(),
            CustomStart = AssistantSettingsResponse.Clock(settings.CustomStartMinute),
            CustomEnd = AssistantSettingsResponse.Clock(settings.CustomEndMinute),
            MaxAssistantTurns = settings.MaxAssistantTurns,
            HumanCooldownMinutes = settings.HumanCooldownMinutes,
            DisclosureMode = settings.DisclosureMode.ToString(),
            DisclosureText = settings.DisclosureText,
            HandoffLine = settings.HandoffLine,
            HandoffKeywords = settings.HandoffKeywords,
            ToneNotes = settings.ToneNotes,
            Language = settings.Language,
            Channels = lines.OrderBy(c => c.DisplayPhoneNumber, StringComparer.Ordinal).Select(c => new AssistantChannelResponse
            {
                ChannelId = c.Id,
                DisplayPhoneNumber = c.DisplayPhoneNumber,
                IsActive = c.IsActive,
                AssistantEnabled = c.AssistantEnabled,
            }).ToList(),
            BlockedByPlatform = tenant?.AssistantBlocked ?? false,
            AiEnabled = tenant?.AiEnabled ?? false,
            GloballyEnabled = await switches.IsEnabledAsync(PlatformSwitch.Assistant, cancellationToken),
            PublishedKnowledgeNumber = published?.Number,
        };
    }

    public async Task<AssistantSettingsResponse> UpdateSettingsAsync(
        Guid tenantId, UpdateAssistantSettingsRequest request, Guid userId, CancellationToken cancellationToken = default)
    {
        var settings = await TrackedSettingsAsync(tenantId, cancellationToken);
        var before = Snapshot(settings);
        settings.Update(new AssistantSettingsValues(
            request.ReplyDelaySeconds ?? 0,
            Enum.Parse<AssistantActiveHours>(request.ActiveHours!, ignoreCase: true),
            AssistantSettingsResponse.Minute(request.CustomStart),
            AssistantSettingsResponse.Minute(request.CustomEnd),
            request.MaxAssistantTurns ?? 0,
            request.HumanCooldownMinutes ?? -1,
            Enum.Parse<AssistantDisclosureMode>(request.DisclosureMode!, ignoreCase: true),
            request.DisclosureText,
            request.HandoffLine,
            request.HandoffKeywords,
            request.ToneNotes,
            request.Language));
        assistant.Audit(AssistantAuditEntry.Record(tenantId, userId, AssistantAuditActions.SettingsChanged,
            new Dictionary<string, object?> { ["from"] = before, ["to"] = Snapshot(settings) }, Now));
        await assistant.SaveChangesAsync(cancellationToken);
        logger.LogInformation(AssistantTrail,
            "Assistant settings of tenant {TenantId} changed by user {UserId}: delay {Delay} s, hours {Hours}, turns {Turns}, cooldown {Cooldown} min, disclosure {Disclosure}, {Keywords} keywords.",
            tenantId, userId, settings.ReplyDelaySeconds, settings.ActiveHours, settings.MaxAssistantTurns, settings.HumanCooldownMinutes, settings.DisclosureMode,
            settings.HandoffKeywords.Count);
        return await GetSettingsAsync(tenantId, cancellationToken);
    }

    public async Task<AssistantSettingsResponse> EnableAsync(Guid tenantId, EnableAssistantRequest request, Guid ownerUserId, CancellationToken cancellationToken = default)
    {
        if (request.TermsVersion?.Trim() != options.TermsVersion)
        {
            throw AssistantException.TermsOutdated(options.TermsVersion);
        }

        var settings = await TrackedSettingsAsync(tenantId, cancellationToken);
        if (settings.Enable(ownerUserId, request.TermsVersion, request.AcceptTerms, request.AcknowledgeLegalBasis, Now))
        {
            // The consent history: every acceptance is a row of its own (the settings row keeps only the latest).
            assistant.Audit(AssistantAuditEntry.Record(tenantId, ownerUserId, AssistantAuditActions.Enabled, new Dictionary<string, object?>
            {
                ["termsVersion"] = settings.TermsVersion,
                ["termsAccepted"] = request.AcceptTerms,
                ["legalBasisAcknowledged"] = request.AcknowledgeLegalBasis,
                ["disclosureMode"] = settings.DisclosureMode.ToString(),
            }, Now));
            await assistant.SaveChangesAsync(cancellationToken);

            // The consent evidence: who, when and which terms (the row keeps them too).
            logger.LogInformation(AssistantTrail,
                "The assistant of tenant {TenantId} was switched on by the owner {UserId} accepting terms {TermsVersion}; legal basis and disclosure acknowledged: {Acknowledged}.",
                tenantId, ownerUserId, settings.TermsVersion, request.AcknowledgeLegalBasis);
        }

        return await GetSettingsAsync(tenantId, cancellationToken);
    }

    public async Task<AssistantSettingsResponse> DisableAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken = default)
    {
        var settings = await TrackedSettingsAsync(tenantId, cancellationToken);
        if (settings.Disable())
        {
            assistant.Audit(AssistantAuditEntry.Record(tenantId, userId, AssistantAuditActions.Disabled, new Dictionary<string, object?>(), Now));
            await assistant.SaveChangesAsync(cancellationToken);
            logger.LogInformation(AssistantTrail, "The assistant of tenant {TenantId} was switched off by user {UserId}.", tenantId, userId);
        }

        return await GetSettingsAsync(tenantId, cancellationToken);
    }

    public async Task<AssistantSettingsResponse> SetChannelAsync(Guid tenantId, Guid channelId, bool enabled, Guid userId, CancellationToken cancellationToken = default)
    {
        var channel = await channels.GetForUpdateAsync(channelId, cancellationToken) ?? throw new ChannelNotFoundException();
        if (channel.SetAssistantEnabled(enabled))
        {
            assistant.Audit(AssistantAuditEntry.Record(tenantId, userId, AssistantAuditActions.ChannelChanged,
                new Dictionary<string, object?> { ["channelId"] = channelId, ["enabled"] = enabled }, Now));
            await channels.UpdateAsync(channel, cancellationToken);
            logger.LogInformation(AssistantTrail, "The assistant on channel {ChannelId} of tenant {TenantId} was set to {Enabled} by user {UserId}.",
                channelId, tenantId, enabled, userId);
        }

        return await GetSettingsAsync(tenantId, cancellationToken);
    }

    public async Task<KnowledgeListResponse> ListKnowledgeAsync(CancellationToken cancellationToken = default)
    {
        var versions = await assistant.ListKnowledgeAsync(KnowledgeListSize, cancellationToken);
        return new KnowledgeListResponse
        {
            Versions = versions.Select(KnowledgeVersionSummary.From).ToList(),
            PublishedNumber = (await assistant.GetPublishedKnowledgeAsync(cancellationToken))?.Number,
            LatestNumber = versions.Count > 0 ? versions.Max(v => v.Number) : null,
        };
    }

    public async Task<KnowledgeVersionResponse> GetKnowledgeAsync(int number, CancellationToken cancellationToken = default)
    {
        var version = await assistant.GetKnowledgeAsync(number, cancellationToken) ?? throw AssistantException.KnowledgeVersionNotFound();
        return KnowledgeVersionResponse.From(version, AssistantKnowledgeJson.Deserialize(version.Content));
    }

    public async Task<KnowledgeVersionResponse> SaveKnowledgeAsync(Guid tenantId, AssistantKnowledge content, Guid userId, CancellationToken cancellationToken = default)
    {
        var branchIds = (await branches.ListAsync(cancellationToken)).Select(b => b.Id).ToHashSet();
        var valid = content.Validated(branchIds);
        var version = await assistant.AddKnowledgeAsync(tenantId, AssistantKnowledgeJson.Serialize(valid), userId, cancellationToken);
        logger.LogInformation(AssistantTrail,
            "Knowledge base version {Number} of tenant {TenantId} saved by user {UserId}: {Services} services, {Promotions} promotions, {Faqs} questions, {Hours} opening hours.",
            version.Number, tenantId, userId, valid.Services.Count, valid.Promotions.Count, valid.Faqs.Count, valid.OpeningHours.Count);
        return KnowledgeVersionResponse.From(version, valid);
    }

    public async Task<KnowledgeVersionResponse> PublishKnowledgeAsync(Guid tenantId, int number, Guid userId, CancellationToken cancellationToken = default)
    {
        var version = await assistant.PublishKnowledgeAsync(number, userId, Now, cancellationToken) ?? throw AssistantException.KnowledgeVersionNotFound();
        assistant.Audit(AssistantAuditEntry.Record(tenantId, userId, AssistantAuditActions.KnowledgePublished, new Dictionary<string, object?> { ["number"] = number }, Now));
        await assistant.SaveChangesAsync(cancellationToken);
        logger.LogInformation(AssistantTrail, "Knowledge base version {Number} of tenant {TenantId} published by user {UserId}.", number, tenantId, userId);
        return KnowledgeVersionResponse.From(version, AssistantKnowledgeJson.Deserialize(version.Content));
    }

    public const int AuditListSize = 100;

    public async Task<IReadOnlyList<AssistantAuditEntryResponse>> ListAuditAsync(CancellationToken cancellationToken = default)
        => [.. (await assistant.ListAuditAsync(AuditListSize, cancellationToken)).Select(AssistantAuditEntryResponse.From)];

    private static Dictionary<string, object?> Snapshot(AssistantSettings s) => new()
    {
        ["replyDelaySeconds"] = s.ReplyDelaySeconds,
        ["activeHours"] = s.ActiveHours.ToString(),
        ["customStart"] = AssistantSettingsResponse.Clock(s.CustomStartMinute),
        ["customEnd"] = AssistantSettingsResponse.Clock(s.CustomEndMinute),
        ["maxAssistantTurns"] = s.MaxAssistantTurns,
        ["humanCooldownMinutes"] = s.HumanCooldownMinutes,
        ["disclosureMode"] = s.DisclosureMode.ToString(),
        ["disclosureText"] = s.DisclosureText,
        ["handoffLine"] = s.HandoffLine,
        ["handoffKeywords"] = s.HandoffKeywords.ToArray(),
        ["toneNotes"] = s.ToneNotes,
    };

    public async Task<ConversationAssistantResponse> GetConversationAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        var conversation = await conversations.GetByIdAsync(conversationId, cancellationToken) ?? throw new ConversationNotFoundException();
        var state = await assistant.GetStateAsync(conversationId, cancellationToken);
        return ConversationAssistantResponse.From(state, await AvailableAsync(conversation, cancellationToken), Now);
    }

    public Task<ConversationAssistantResponse> TakeOverAsync(Guid tenantId, Guid conversationId, Guid userId, CancellationToken cancellationToken = default)
        => ChangeAsync(tenantId, conversationId, userId, AssistantMetrics.TakenOver, (conversation, state, _) =>
        {
            state.TakeOver(userId, Now);
            return Task.FromResult(true);
        }, cancellationToken);

    public Task<ConversationAssistantResponse> HandBackAsync(Guid tenantId, Guid conversationId, Guid userId, CancellationToken cancellationToken = default)
        => ChangeAsync(tenantId, conversationId, userId, AssistantMetrics.HandedBack, (conversation, state, settings) =>
        {
            // A customer waiting for a reply inside the window gets one after the usual delay; otherwise the next message arms it.
            var waiting = conversation.Status == ConversationStatus.Open && conversation.AwaitingReply
                          && Domain.Common.CustomerServiceWindow.IsOpen(conversation.LastInboundAt, Now);
            return Task.FromResult(state.HandBack(Now, waiting ? settings.ReplyDelay : null));
        }, cancellationToken);

    // Under the conversation's lock (the ingestion order), like every change of the assistant's state.
    private async Task<ConversationAssistantResponse> ChangeAsync(
        Guid tenantId, Guid conversationId, Guid userId, string metric, Func<Conversation, ConversationAssistant, AssistantSettings, Task<bool>> change,
        CancellationToken cancellationToken)
    {
        var read = await conversations.GetByIdAsync(conversationId, cancellationToken) ?? throw new ConversationNotFoundException();
        var settings = await assistant.GetSettingsAsync(cancellationToken);
        if (settings is not { Enabled: true } || !await AvailableAsync(read, cancellationToken))
        {
            throw AssistantException.NotAvailable();
        }

        ConversationAssistant? result = null;
        var found = await conversations.ChangeSerializedWithIngestionAsync(tenantId, conversationId, async conversation =>
        {
            var state = await assistant.FindStateForUpdateAsync(conversationId, CancellationToken.None);
            if (state is null)
            {
                state = ConversationAssistant.Create(conversation.TenantId, conversation.Id, conversation.IsSandbox);
                assistant.AddState(state);
            }

            result = state;
            if (!await change(conversation, state, settings))
            {
                return; // nothing to change (a hand-back of a conversation the assistant already has)
            }

            AssistantSignals.Touch(conversation, Now);
            await assistant.CountAsync(conversation.TenantId, conversation.IsSandbox, metric, zone.LocalDate(Now), CancellationToken.None);
        }, cancellationToken);

        if (!found || result is null)
        {
            throw new ConversationNotFoundException();
        }

        logger.LogInformation(AssistantTrail, "Conversation {ConversationId} of tenant {TenantId}: {Action} by user {UserId}.", conversationId, tenantId, metric, userId);
        return ConversationAssistantResponse.From(result, available: true, Now);
    }

    private async Task<bool> AvailableAsync(Conversation conversation, CancellationToken cancellationToken)
        => (await channels.GetByIdAsync(conversation.ChannelId, cancellationToken))?.AssistantEnabled == true && !conversation.IsImported;

    private async Task<AssistantSettings> TrackedSettingsAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var settings = await assistant.GetSettingsAsync(cancellationToken);
        if (settings is null)
        {
            settings = AssistantSettings.Create(tenantId);
            assistant.AddSettings(settings);
        }

        return settings;
    }
}
