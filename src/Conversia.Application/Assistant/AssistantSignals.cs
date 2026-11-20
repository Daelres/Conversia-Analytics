using Conversia.Application.Time;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace Conversia.Application.Assistant;

/// <summary>
/// What the message paths tell the assistant (docs/ASSISTANT.md, "State machine"). Both calls run inside the caller's transaction, under the
/// conversation's lock (ingestion, or the record step of the send path), so the assistant's state changes with the messages that caused it:
/// a customer message arms the reply timer, a person's message cancels it.
/// </summary>
public interface IAssistantSignals
{
    /// <summary>
    /// Unanswered customer messages were stored in <paramref name="conversation"/> on <paramref name="channel"/>; <paramref name="texts"/> are
    /// their texts (a request for a person makes the timer fire within seconds).
    /// </summary>
    Task CustomerWroteAsync(Conversation conversation, WhatsAppChannel channel, DateTime now, IReadOnlyList<string?> texts, CancellationToken cancellationToken = default);

    /// <summary>
    /// A person's message was stored (sent from Conversia, or an echo of the phone line). <paramref name="echoTexts"/>: the texts of the echoes
    /// of this delivery; an echo of the assistant's own pending send (same text, within its window) is not a person.
    /// </summary>
    Task HumanWroteAsync(Conversation conversation, DateTime now, IReadOnlyList<string?>? echoTexts = null, CancellationToken cancellationToken = default);
}

public sealed class AssistantSignals(IAssistantRepository assistant, TenantTimeZone zone, ILogger<AssistantSignals> logger, AssistantOptions? options = null) : IAssistantSignals
{
    private readonly AssistantOptions _options = options ?? new AssistantOptions();
    private AssistantSettings? _settings;
    private bool _settingsRead;

    /// <summary>What an echo is matched by: SHA-256 (hex) of the text as sent, cleaned the way the send path stores it.</summary>
    public static string TextHash(string text)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text.Trim()))).ToLowerInvariant();

    public async Task CustomerWroteAsync(
        Conversation conversation, WhatsAppChannel channel, DateTime now, IReadOnlyList<string?> texts, CancellationToken cancellationToken = default)
    {
        // Imported history and closed conversations are never answered; a line or a company without the assistant arms nothing (the job
        // re-checks every switch when the timer fires).
        if (conversation.IsImported || conversation.Status != ConversationStatus.Open || !conversation.AwaitingReply || !channel.AssistantEnabled)
        {
            return;
        }

        var settings = await SettingsAsync(cancellationToken);
        if (settings is not { Enabled: true })
        {
            return;
        }

        var state = await assistant.FindStateForUpdateAsync(conversation.Id, cancellationToken);
        if (state is null)
        {
            state = ConversationAssistant.Create(conversation.TenantId, conversation.Id, conversation.IsSandbox);
            assistant.AddState(state);

            // A person was already handling it before the assistant had any state here (switched on meanwhile): their hold applies.
            if (conversation.LastOutboundAt is { } answered)
            {
                state.HumanWrote(answered, settings.HumanCooldown);
            }
        }

        var wasWaiting = state.State == AssistantState.Waiting;
        if (state.CustomerWrote(now, settings.ReplyDelay))
        {
            if (texts.Any(t => t is not null && AssistantRules.AsksForHuman(t)))
            {
                state.Hasten(now, TimeSpan.FromSeconds(_options.HumanRequestDelaySeconds));
            }

            if (!wasWaiting)
            {
                Touch(conversation, now);
                await assistant.CountAsync(conversation.TenantId, conversation.IsSandbox, AssistantMetrics.TimerArmed, zone.LocalDate(now), cancellationToken);
            }
        }
    }

    public async Task HumanWroteAsync(Conversation conversation, DateTime now, IReadOnlyList<string?>? echoTexts = null, CancellationToken cancellationToken = default)
    {
        var state = await assistant.FindStateForUpdateAsync(conversation.Id, cancellationToken);
        if (echoTexts is { Count: > 0 } && state is not null
            && echoTexts.All(t => t is not null && state.IsOwnEcho(TextHash(t), now, _options.OwnEchoWindow)))
        {
            return; // the assistant's own message came back before its send was recorded: the record step claims it
        }

        var settings = await SettingsAsync(cancellationToken);
        if (state is null)
        {
            if (settings is not { Enabled: true } || conversation.IsImported)
            {
                return;
            }

            // A person answers a conversation the assistant never armed: it starts held, so the next customer message does not wake it.
            state = ConversationAssistant.Create(conversation.TenantId, conversation.Id, conversation.IsSandbox);
            assistant.AddState(state);
        }

        var cooldown = settings?.HumanCooldown ?? TimeSpan.FromMinutes(AssistantSettings.DefaultCooldownMinutes);
        var cancelled = state.State == AssistantState.Waiting;
        state.HumanWrote(now, cooldown);
        Touch(conversation, now);
        if (cancelled)
        {
            logger.LogDebug("The assistant timer of conversation {ConversationId} was cancelled: a person answered.", conversation.Id);
            await assistant.CountAsync(conversation.TenantId, conversation.IsSandbox, AssistantMetrics.DiscardedBecause(AssistantDiscardReasons.Answered),
                zone.LocalDate(now), cancellationToken);
        }
    }

    // The inbox's change token and delta read the conversation's last change (docs/MESSAGING.md, "Inbox"): a badge that changed is a change.
    public static void Touch(Conversation conversation, DateTime now) => conversation.UpdatedAt = now;

    private async Task<AssistantSettings?> SettingsAsync(CancellationToken cancellationToken)
    {
        if (!_settingsRead)
        {
            _settings = await assistant.GetSettingsAsync(cancellationToken);
            _settingsRead = true;
        }

        return _settings;
    }
}
