using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Conversia.Domain.Common;
using Conversia.Domain.Enums;
using Conversia.Domain.Exceptions;

namespace Conversia.Domain.Entities;

/// <summary>
/// How the AI sales assistant behaves for one company (docs/ASSISTANT.md, "Settings"). Off by default; switching it on needs the company
/// owner to accept the assistant terms and acknowledge the legal basis and the disclosure rules, recorded with who and when. Which lines it
/// answers on is a flag of each line (<see cref="WhatsAppChannel.AssistantEnabled"/>).
/// </summary>
public partial class AssistantSettings : BaseEntity
{
    public const int MinReplyDelaySeconds = 30;
    public const int MaxReplyDelaySeconds = 900;
    public const int DefaultReplyDelaySeconds = 60;
    public const int MinTurns = 1;
    public const int MaxTurns = 50;
    public const int DefaultMaxTurns = 8;
    public const int MaxCooldownMinutes = 1440;
    public const int DefaultCooldownMinutes = 60;
    public const int MinutesPerDay = 1440;
    public const int TermsVersionMaxLength = 50;
    public const int ToneNotesMaxLength = 500;
    public const int LineMaxLength = 300;
    public const int KeywordMaxLength = 120;
    public const int MaxKeywords = 50;
    public const int MisunderstandingLimit = 3;
    public const string SupportedLanguage = "es-CO";

    /// <summary>A keyword that starts with this prefix is a regular expression (matched without backtracking, with a timeout).</summary>
    public const string PatternPrefix = "re:";

    public const string DefaultHandoffLine = "Ya le paso tu mensaje a un asesor para que te ayude con esto.";

    public const string DefaultDisclosureText = "Te escribe el asistente virtual de la empresa. Si prefieres hablar con una persona, dímelo y te comunico con un asesor.";

    /// <summary>The words a complaint, a legal threat, a refund or a cancellation usually carry; the company edits the list.</summary>
    public static readonly IReadOnlyList<string> DefaultHandoffKeywords =
    [
        "queja", "reclamo", "demanda", "abogado", "denuncia", "superintendencia", "reembolso", "devolución", "devuelvan", "cancelar", "estafa",
        "fraude", "pésimo", "groser",
    ];

    public static readonly string ReplyDelayRuleMessage =
        $"La espera antes de responder debe estar entre {MinReplyDelaySeconds} y {MaxReplyDelaySeconds} segundos.";

    public static readonly string MaxTurnsRuleMessage = $"Las respuestas seguidas del asistente deben estar entre {MinTurns} y {MaxTurns}.";

    public static readonly string CooldownRuleMessage = $"La pausa después de que escribe una persona debe estar entre 0 y {MaxCooldownMinutes} minutos.";

    public static readonly string CustomHoursRuleMessage = "Con horario personalizado indica la hora de inicio y la de fin (HH:mm), y que no sean iguales.";

    public static readonly string ToneNotesRuleMessage = $"Las notas de tono {TextRules.RuleText(ToneNotesMaxLength, required: false)}, sin enlaces.";

    public static readonly string HandoffLineRuleMessage = $"La frase de traspaso {TextRules.RuleText(LineMaxLength)}, sin enlaces.";

    public static readonly string DisclosureTextRuleMessage = $"El aviso de asistente virtual {TextRules.RuleText(LineMaxLength)}, sin enlaces.";

    public static readonly string KeywordsRuleMessage =
        $"Las palabras de traspaso son a lo sumo {MaxKeywords}, de hasta {KeywordMaxLength} caracteres cada una; un patrón empieza con «{PatternPrefix}» y debe ser una expresión regular válida.";

    public static readonly string LanguageRuleMessage = $"Por ahora el asistente solo habla español de Colombia ({SupportedLanguage}).";

    public const string TermsRuleMessage =
        "Para activar el asistente, la persona propietaria debe aceptar los términos del asistente y confirmar la base legal y el aviso de asistente virtual.";

    public Guid TenantId { get; private set; }

    /// <summary>The company's switch (the administrator's kill switches and each line's flag apply on top).</summary>
    public bool Enabled { get; private set; }

    public string? TermsVersion { get; private set; }

    public DateTime? TermsAcceptedAt { get; private set; }

    public Guid? TermsAcceptedBy { get; private set; }

    public int ReplyDelaySeconds { get; private set; } = DefaultReplyDelaySeconds;

    public AssistantActiveHours ActiveHours { get; private set; } = AssistantActiveHours.Always;

    /// <summary>Local minute of the day (America/Bogota) the custom window opens; set only with <see cref="AssistantActiveHours.Custom"/>.</summary>
    public int? CustomStartMinute { get; private set; }

    public int? CustomEndMinute { get; private set; }

    public int MaxAssistantTurns { get; private set; } = DefaultMaxTurns;

    public int HumanCooldownMinutes { get; private set; } = DefaultCooldownMinutes;

    public AssistantDisclosureMode DisclosureMode { get; private set; } = AssistantDisclosureMode.OnRequest;

    public string DisclosureText { get; private set; } = DefaultDisclosureText;

    public string HandoffLine { get; private set; } = DefaultHandoffLine;

    public List<string> HandoffKeywords { get; private set; } = [.. DefaultHandoffKeywords];

    public string? ToneNotes { get; private set; }

    public string Language { get; private set; } = SupportedLanguage;

    public TimeSpan ReplyDelay => TimeSpan.FromSeconds(ReplyDelaySeconds);

    public TimeSpan HumanCooldown => TimeSpan.FromMinutes(HumanCooldownMinutes);

    public static AssistantSettings Create(Guid tenantId) => new() { TenantId = TextRules.RequiredId(tenantId, "La empresa es obligatoria.") };

    /// <summary>Applies every setting but the switch; nothing changes unless all of them are valid.</summary>
    public void Update(AssistantSettingsValues values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.ReplyDelaySeconds is < MinReplyDelaySeconds or > MaxReplyDelaySeconds)
        {
            throw new DomainException(ReplyDelayRuleMessage);
        }

        if (values.MaxAssistantTurns is < MinTurns or > MaxTurns)
        {
            throw new DomainException(MaxTurnsRuleMessage);
        }

        if (values.HumanCooldownMinutes is < 0 or > MaxCooldownMinutes)
        {
            throw new DomainException(CooldownRuleMessage);
        }

        if (!Enum.IsDefined(values.ActiveHours) || !Enum.IsDefined(values.DisclosureMode))
        {
            throw new DomainException("El horario o el modo de aviso no son válidos.");
        }

        var custom = values.ActiveHours == AssistantActiveHours.Custom;
        if (custom && (values.CustomStartMinute is not (>= 0 and < MinutesPerDay) || values.CustomEndMinute is not (>= 0 and < MinutesPerDay)
                       || values.CustomStartMinute == values.CustomEndMinute))
        {
            throw new DomainException(CustomHoursRuleMessage);
        }

        if (values.Language is not null && values.Language != SupportedLanguage)
        {
            throw new DomainException(LanguageRuleMessage);
        }

        var handoffLine = CustomerLine(values.HandoffLine, HandoffLineRuleMessage);
        var disclosure = CustomerLine(values.DisclosureText, DisclosureTextRuleMessage);
        var tone = values.ToneNotes is null ? null : LinkRules.Clean(values.ToneNotes);
        if (!TextRules.IsValidOptional(tone, ToneNotesMaxLength) || (tone is not null && LinkRules.HasLink(tone)))
        {
            throw new DomainException(ToneNotesRuleMessage);
        }

        var keywords = Keywords(values.HandoffKeywords ?? []);

        ReplyDelaySeconds = values.ReplyDelaySeconds;
        ActiveHours = values.ActiveHours;
        CustomStartMinute = custom ? values.CustomStartMinute : null;
        CustomEndMinute = custom ? values.CustomEndMinute : null;
        MaxAssistantTurns = values.MaxAssistantTurns;
        HumanCooldownMinutes = values.HumanCooldownMinutes;
        DisclosureMode = values.DisclosureMode;
        DisclosureText = disclosure;
        HandoffLine = handoffLine;
        HandoffKeywords = keywords;
        ToneNotes = string.IsNullOrEmpty(tone) ? null : tone;
        Language = SupportedLanguage;
    }

    /// <summary>The owner switches the assistant on, accepting <paramref name="termsVersion"/>. Returns false when it already was on with those terms.</summary>
    public bool Enable(Guid ownerUserId, string? termsVersion, bool termsAccepted, bool legalBasisAcknowledged, DateTime now)
    {
        if (!termsAccepted || !legalBasisAcknowledged || ownerUserId == Guid.Empty || !TextRules.IsValidRequired(termsVersion, TermsVersionMaxLength))
        {
            throw new DomainException(TermsRuleMessage);
        }

        var version = termsVersion!.Trim();
        if (Enabled && TermsVersion == version)
        {
            return false;
        }

        Enabled = true;
        TermsVersion = version;
        TermsAcceptedBy = ownerUserId;
        TermsAcceptedAt = UtcTime.Normalize(now);
        return true;
    }

    /// <summary>Returns false when it already was off.</summary>
    public bool Disable()
    {
        if (!Enabled)
        {
            return false;
        }

        Enabled = false;
        return true;
    }

    /// <summary>Whether the local time <paramref name="localMinute"/> falls within the custom window (which may cross midnight).</summary>
    public bool InCustomWindow(int localMinute)
    {
        if (CustomStartMinute is not { } start || CustomEndMinute is not { } end)
        {
            return false;
        }

        return start < end ? localMinute >= start && localMinute < end : localMinute >= start || localMinute < end;
    }

    // A line the customer reads: required, one line, no link (the knowledge base's allowed domain is for its own answers).
    private static string CustomerLine(string? value, string message)
    {
        var line = value is null ? null : LinkRules.Clean(value);
        return TextRules.IsValidRequired(line, LineMaxLength) && !LinkRules.HasLink(line!) ? line! : throw new DomainException(message);
    }

    private static List<string> Keywords(IReadOnlyList<string> raw)
    {
        var keywords = new List<string>();
        foreach (var entry in raw)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            var trimmed = entry.Trim();
            string keyword;
            if (trimmed.StartsWith(PatternPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var pattern = trimmed[PatternPrefix.Length..].Trim();
                if (pattern.Length == 0)
                {
                    throw new DomainException(KeywordsRuleMessage);
                }

                try
                {
                    CompilePattern(pattern);
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
                {
                    throw new DomainException(KeywordsRuleMessage);
                }

                keyword = PatternPrefix + pattern;
            }
            else
            {
                keyword = Normalize(trimmed);
            }

            if (!TextRules.IsValidRequired(keyword, KeywordMaxLength))
            {
                throw new DomainException(KeywordsRuleMessage);
            }

            if (!keywords.Contains(keyword))
            {
                keywords.Add(keyword);
            }
        }

        return keywords.Count > MaxKeywords ? throw new DomainException(KeywordsRuleMessage) : keywords;
    }

    /// <summary>Lower case without accents, single spaces: how customer text and keywords are compared.</summary>
    public static string Normalize(string text)
    {
        var decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(char.IsWhiteSpace(c) ? ' ' : c);
            }
        }

        return SpaceRun().Replace(builder.ToString().Normalize(NormalizationForm.FormC), " ").Trim();
    }

    /// <summary>A keyword pattern (<see cref="PatternPrefix"/>), compiled without backtracking and with a timeout: a company's pattern can never stall the job.</summary>
    public static Regex CompilePattern(string pattern)
        => new(pattern, RegexOptions.NonBacktracking | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpaceRun();
}

/// <summary>Every setting of <see cref="AssistantSettings.Update"/>, as typed (validated there).</summary>
public sealed record AssistantSettingsValues(
    int ReplyDelaySeconds,
    AssistantActiveHours ActiveHours,
    int? CustomStartMinute,
    int? CustomEndMinute,
    int MaxAssistantTurns,
    int HumanCooldownMinutes,
    AssistantDisclosureMode DisclosureMode,
    string? DisclosureText,
    string? HandoffLine,
    IReadOnlyList<string>? HandoffKeywords,
    string? ToneNotes,
    string? Language);
