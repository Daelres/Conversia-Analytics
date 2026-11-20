using System.Text.RegularExpressions;
using Conversia.Domain.Common;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;

namespace Conversia.Application.Assistant;

/// <summary>
/// The deterministic part of the assistant (docs/ASSISTANT.md, "Handoff"): the triggers that pass a conversation to a person before any model
/// is asked, the honest answer to "am I talking to a bot?", and the last check of a text before it is sent. All on normalized text (lower
/// case, no accents); every pattern is linear (no backtracking).
/// </summary>
public static partial class AssistantRules
{
    public const int ReplyMaxLength = 1000;

    /// <summary>Why the unanswered customer messages pass the conversation to a person now; null when the assistant may answer.</summary>
    public static string? HandoffReason(IReadOnlyList<string> unansweredCustomerTexts, AssistantSettings settings, ConversationAssistant state)
    {
        if (state.AssistantTurns >= settings.MaxAssistantTurns)
        {
            return AssistantHandoffReasons.MaxTurns;
        }

        if (state.MisunderstandingCount >= AssistantSettings.MisunderstandingLimit)
        {
            return AssistantHandoffReasons.Misunderstanding;
        }

        if (unansweredCustomerTexts.Any(AsksForHuman))
        {
            return AssistantHandoffReasons.HumanRequested;
        }

        if (unansweredCustomerTexts.Any(LooksLikeInjection))
        {
            return AssistantHandoffReasons.InjectionSuspected;
        }

        return unansweredCustomerTexts.Any(t => MatchesKeyword(t, settings.HandoffKeywords)) ? AssistantHandoffReasons.Keyword : null;
    }

    /// <summary>The customer asks to talk to a person.</summary>
    public static bool AsksForHuman(string text)
    {
        var folded = Fold(text);
        return HumanRequest().IsMatch(folded) || (BareHumanRequest().IsMatch(folded) && !AssistantQuestion().IsMatch(folded));
    }

    /// <summary>The customer asks whether they are talking to a bot, a machine or a person.</summary>
    public static bool AsksIfAssistant(string text) => !AsksForHuman(text) && AssistantQuestion().IsMatch(Fold(text));

    /// <summary>The text tries to give the assistant instructions (prompt injection): it is passed to a person, never to the model.</summary>
    public static bool LooksLikeInjection(string text) => Injection().IsMatch(Fold(text));

    /// <summary>One of the company's keywords (at the start of a word) or patterns (<c>re:</c>) is in the text.</summary>
    public static bool MatchesKeyword(string text, IReadOnlyList<string> keywords)
    {
        var normalized = " " + Fold(text);
        foreach (var keyword in keywords)
        {
            if (keyword.StartsWith(AssistantSettings.PatternPrefix, StringComparison.Ordinal))
            {
                try
                {
                    var pattern = AssistantSettings.CompilePattern(keyword[AssistantSettings.PatternPrefix.Length..]);
                    if (pattern.IsMatch(text) || pattern.IsMatch(normalized))
                    {
                        return true;
                    }
                }
                catch (Exception ex) when (ex is RegexMatchTimeoutException or ArgumentException)
                {
                    return true; // a pattern that cannot be evaluated errs on the side of a person
                }
            }
            else if (WordStart(normalized, AssistantSettings.Normalize(keyword)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The text the assistant would send, cleaned; null when it must not be sent: empty or too long, a link outside the allowed domain, a
    /// forbidden phrase, or a claim to be a person.
    /// </summary>
    public static string? CheckReply(string? text, AssistantKnowledge? knowledge)
    {
        if (text is null)
        {
            return null;
        }

        var cleaned = LinkRules.CleanMultiline(text);
        if (!TextRules.IsValidRequired(cleaned, ReplyMaxLength) || !LinkRules.OnlyLinksTo(cleaned, knowledge?.AllowedLinkDomain) || ClaimsToBeHuman(cleaned))
        {
            return null;
        }

        var normalized = Fold(cleaned);
        return (knowledge?.ForbiddenPhrases ?? []).Any(p => normalized.Contains(Fold(p), StringComparison.Ordinal)) ? null : cleaned;
    }

    /// <summary>The text claims a person wrote it, or denies being the assistant.</summary>
    public static bool ClaimsToBeHuman(string text)
    {
        var folded = Fold(text);
        return HumanClaim().IsMatch(folded)
               || NameIntro().Matches(folded).Any(m => !NotANameAfterSoy.Contains(m.Groups["word"].Value));
    }

    /// <summary>
    /// The text as the checks read it: format and control characters (zero-width, direction marks) removed, compatibility forms folded (full
    /// width, ligatures), the common Cyrillic and Greek look-alikes of Latin letters mapped back, then lower case without accents.
    /// </summary>
    public static string Fold(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length);
        foreach (var c in text.Normalize(System.Text.NormalizationForm.FormKC))
        {
            var category = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c);
            if (category is System.Globalization.UnicodeCategory.Format || (char.IsControl(c) && !char.IsWhiteSpace(c)))
            {
                continue;
            }

            builder.Append(Confusables.TryGetValue(c, out var latin) ? latin : c);
        }

        return AssistantSettings.Normalize(builder.ToString());
    }

    private static readonly Dictionary<char, char> Confusables = new()
    {
        ['а'] = 'a', ['е'] = 'e', ['о'] = 'o', ['р'] = 'p', ['с'] = 'c', ['у'] = 'y', ['х'] = 'x', ['і'] = 'i', ['ј'] = 'j', ['ѕ'] = 's',
        ['А'] = 'A', ['Е'] = 'E', ['О'] = 'O', ['Р'] = 'P', ['С'] = 'C', ['Х'] = 'X', ['Н'] = 'H', ['К'] = 'K', ['М'] = 'M', ['Т'] = 'T', ['В'] = 'B',
        ['ο'] = 'o', ['α'] = 'a', ['ε'] = 'e', ['ι'] = 'i', ['ν'] = 'v', ['ρ'] = 'p', ['τ'] = 't', ['υ'] = 'u', ['Ο'] = 'O', ['Α'] = 'A', ['Ε'] = 'E',
    };

    private static bool WordStart(string normalizedText, string keyword)
    {
        if (keyword.Length == 0)
        {
            return false;
        }

        for (var at = normalizedText.IndexOf(keyword, StringComparison.Ordinal); at >= 0; at = normalizedText.IndexOf(keyword, at + 1, StringComparison.Ordinal))
        {
            if (at == 0 || !char.IsLetterOrDigit(normalizedText[at - 1]))
            {
                return true;
            }
        }

        return false;
    }

    private const RegexOptions Linear = RegexOptions.NonBacktracking | RegexOptions.CultureInvariant;

    [GeneratedRegex(@"\b(hablar|hable|habla|comunicar|comunica|comunicame|comuniquen|pasar|pasame|pasa|pasas|pasan|pasen|comunicas|atender|atienda|atiende|quiero|necesito|prefiero|ponme|conectar|conectame|contactar|contactame)\b[^.?!]{0,30}\b(asesor|asesora|asesores|persona|humano|humana|agente|operador|operadora|alguien|representante|ejecutivo|ejecutiva|vendedor|vendedora|supervisor|supervisora|gerente|encargado|encargada)\b|\bcon (un|una|el|la|al) (asesor|asesora|representante|ejecutivo|ejecutiva|vendedor|vendedora|supervisor|supervisora|humano|agente)\b|\bllam(e|en)(me|nos)\b|\bllamame\b", Linear)]
    private static partial Regex HumanRequest();

    // Bare mentions ("una persona", "alguien real"): a request only when the message is not asking whether it talks to one.
    [GeneratedRegex(@"\bpersona real\b|\balguien real\b|\buna persona\b|\bun humano\b", Linear)]
    private static partial Regex BareHumanRequest();

    [GeneratedRegex(@"\b(eres|sos|es esto|esto es|estoy hablando con|hablo con|me responde|me contesta|me escribe)\b[^.?!]{0,30}\b(bot|chatbot|robot|maquina|ia|inteligencia artificial|humano|humana|persona|asistente virtual|automatico|automatica|programa)\b|\brespuesta automatica\b|^\s*(¿|\?)?\s*es (un |una )?(bot|robot|ia|maquina|respuesta automatica)\b", Linear)]
    private static partial Regex AssistantQuestion();

    [GeneratedRegex(@"\b(ignora|ignorar|olvida|olvidar|omite|salta)\b[^.]{0,30}\b(instrucciones|reglas|indicaciones|prompt)\b|\b(system prompt|prompt del sistema|tu prompt|tus instrucciones)\b|\ba partir de ahora (eres|seras|actua)|\bactua como\b|\bmodo (desarrollador|dios|developer)\b|\bjailbreak\b|\bignore (all |the |any )?(previous |prior |above )?instructions\b|\byou are now\b|\bdeveloper mode\b", Linear)]
    private static partial Regex Injection();

    [GeneratedRegex(@"\b(soy|somos) (un |una )?(persona|humano|humana|ser humano|asesor|asesora)\b|\bno soy (un |una )?(bot|robot|asistente|ia|maquina|programa|inteligencia artificial)\b|\b(te habla|te escribe|te atiende) (un |una )?(persona|ser humano|humano|humana)\b|\bsoy yo\b|\bte atiendo personalmente\b|\b(mi nombre es|me llamo)\b", Linear)]
    private static partial Regex HumanClaim();

    // "Soy Camila, ..." : a first name introduced with a comma (the word is checked in code: the engine has no look-ahead).
    [GeneratedRegex(@"\bsoy (?<word>[a-z]{3,}) ?,", Linear)]
    private static partial Regex NameIntro();

    private static readonly HashSet<string> NotANameAfterSoy = new(StringComparer.Ordinal) { "asistente", "virtual", "the", "parte", "nuevo", "nueva" };
}

/// <summary>When the assistant may answer, by the company's setting (docs/ASSISTANT.md, "Settings").</summary>
public static class AssistantHours
{
    /// <summary>
    /// <see cref="AssistantActiveHours.OutsideBusinessHours"/> answers only when the knowledge base says the branch is closed; hours it does
    /// not know count as open (the assistant stays quiet rather than answer during business hours by mistake).
    /// </summary>
    public static bool IsActive(AssistantSettings settings, AssistantKnowledge? knowledge, Guid branchId, DateTime utcNow, TimeZoneInfo zone)
    {
        if (settings.ActiveHours == AssistantActiveHours.Always)
        {
            return true;
        }

        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), zone);
        var minute = local.Hour * 60 + local.Minute;
        return settings.ActiveHours == AssistantActiveHours.Custom
            ? settings.InCustomWindow(minute)
            : knowledge?.IsOpen(branchId, DateOnly.FromDateTime(local), minute) == false;
    }
}

/// <summary>The facts the job re-checks under the conversation lock before preparing an answer and again before sending it.</summary>
public sealed record AssistantFacts(
    bool GlobalEnabled,
    bool CompanyBlocked,
    bool CompanyActive,
    bool AiEnabled,
    bool AssistantEnabled,
    bool ChannelActive,
    bool ChannelAssistantEnabled,
    bool SandboxMatches,
    bool ConversationOpen,
    bool AwaitingReply,
    bool WindowOpen,
    bool WithinActiveHours,
    bool Admitted);

/// <summary>What to do with a fired timer.</summary>
public sealed record AssistantDecision(AssistantDecisionKind Kind, string? Reason)
{
    public static readonly AssistantDecision Proceed = new(AssistantDecisionKind.Proceed, null);

    public static AssistantDecision Discard(string reason) => new(AssistantDecisionKind.Discard, reason);

    public static AssistantDecision HandOff(string reason) => new(AssistantDecisionKind.HandOff, reason);
}

public enum AssistantDecisionKind
{
    Proceed,
    Discard,
    HandOff,
}

/// <summary>The codes of a discarded timer (counted as <c>discarded.{code}</c>).</summary>
public static class AssistantDiscardReasons
{
    public const string GlobalOff = "global_off";
    public const string CompanyBlocked = "company_blocked";
    public const string CompanyInactive = "company_inactive";
    public const string AiOff = "ai_off";
    public const string AssistantOff = "assistant_off";
    public const string ChannelOff = "channel_off";
    public const string SandboxMismatch = "sandbox_mismatch";
    public const string ConversationClosed = "conversation_closed";
    public const string Answered = "answered";
    public const string OutsideHours = "outside_hours";
}

public static class AssistantPolicy
{
    /// <summary>The kill switches first, then the company, the line, the conversation, the window, the hours and the quota.</summary>
    public static AssistantDecision Check(AssistantFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return facts switch
        {
            { GlobalEnabled: false } => AssistantDecision.Discard(AssistantDiscardReasons.GlobalOff),
            { CompanyBlocked: true } => AssistantDecision.Discard(AssistantDiscardReasons.CompanyBlocked),
            { CompanyActive: false } => AssistantDecision.Discard(AssistantDiscardReasons.CompanyInactive),
            { AiEnabled: false } => AssistantDecision.Discard(AssistantDiscardReasons.AiOff),
            { AssistantEnabled: false } => AssistantDecision.Discard(AssistantDiscardReasons.AssistantOff),
            { ChannelActive: false } or { ChannelAssistantEnabled: false } => AssistantDecision.Discard(AssistantDiscardReasons.ChannelOff),
            { SandboxMatches: false } => AssistantDecision.Discard(AssistantDiscardReasons.SandboxMismatch),
            { ConversationOpen: false } => AssistantDecision.Discard(AssistantDiscardReasons.ConversationClosed),
            { AwaitingReply: false } => AssistantDecision.Discard(AssistantDiscardReasons.Answered),
            // The assistant never sends a template on its own: past the window a person decides.
            { WindowOpen: false } => AssistantDecision.HandOff(AssistantHandoffReasons.WindowClosed),
            { WithinActiveHours: false } => AssistantDecision.Discard(AssistantDiscardReasons.OutsideHours),
            { Admitted: false } => AssistantDecision.HandOff(AssistantHandoffReasons.QuotaExhausted),
            _ => AssistantDecision.Proceed,
        };
    }
}
