using System.Text.RegularExpressions;
using Conversia.Domain.Common;
using Conversia.Domain.Enums;
using Conversia.Domain.Exceptions;

namespace Conversia.Domain.Entities;

/// <summary>
/// A WhatsApp message template of one channel, as its provider last described it (docs/MESSAGING.md, "Templates"). Templates are created
/// and approved in the 360dialog Hub; Conversia only keeps a copy to offer them and to send them. The copy holds the business's own text
/// and the names of its placeholders, never customer data: the values a person types for a send live only on the message sent.
/// </summary>
public class MessageTemplate : BaseEntity
{
    /// <summary>Meta's limit for a template name.</summary>
    public const int NameMaxLength = 512;

    public const int LanguageMaxLength = 15;

    /// <summary>Meta's limit for the body of a template.</summary>
    public const int BodyMaxLength = 1024;

    /// <summary>More placeholders than a person can fill in one form: such a template is kept but not offered.</summary>
    public const int MaxParameters = 20;

    /// <summary>Conversia's limit for one value (Meta publishes none for a body parameter; the rendered body stays a readable message).</summary>
    public const int ParameterMaxLength = 500;

    public const string NameRuleMessage = "El nombre de la plantilla debe tener entre 1 y 512 caracteres: letras minúsculas, dígitos o '_'.";
    public const string LanguageRuleMessage = "El idioma de la plantilla no es válido (por ejemplo, es o es_CO).";
    public const string ParameterCountRuleMessage = "La cantidad de valores no coincide con los campos de la plantilla.";

    public static readonly string ParameterRuleMessage =
        $"Cada valor de la plantilla es obligatorio, tiene como máximo {ParameterMaxLength} caracteres y no admite saltos de línea, tabulaciones ni más de cuatro espacios seguidos.";

    public Guid TenantId { get; private set; }

    public Guid ChannelId { get; private set; }

    /// <summary>The template's name at Meta (lower case, digits and '_').</summary>
    public string Name { get; private set; } = string.Empty;

    /// <summary>Meta's language code (<c>es</c>, <c>es_CO</c>). A name has one row per language.</summary>
    public string Language { get; private set; } = string.Empty;

    public TemplateCategory Category { get; private set; }

    public TemplateStatus Status { get; private set; }

    /// <summary>The body text with its placeholders (<c>{{1}}</c> or <c>{{first_name}}</c>); null when the provider gave none.</summary>
    public string? BodyText { get; private set; }

    /// <summary>The placeholders of the body, in the order their values are sent: by number when positional, by first appearance when named.</summary>
    public string[] ParameterNames { get; private set; } = [];

    /// <summary>
    /// False for a template Conversia cannot send yet: a header with media or a variable, buttons with variables, an authentication code,
    /// a carousel, or too many placeholders. It is listed but not offered.
    /// </summary>
    public bool IsSupported { get; private set; }

    /// <summary>When the provider last listed it.</summary>
    public DateTime SyncedAt { get; private set; }

    /// <summary>When its status last changed by a status notification of the provider; null when only syncs changed it.</summary>
    public DateTime? StatusChangedAt { get; private set; }

    public int ParameterCount => ParameterNames.Length;

    /// <summary>Named placeholders (<c>{{first_name}}</c>) are sent with their names; positional ones (<c>{{1}}</c>) only in order.</summary>
    public bool NamedParameters => ParameterNames.Length > 0 && !ParameterNames.All(TemplatePlaceholders.IsPositional);

    public bool CanBeSent => Status == TemplateStatus.Approved && IsSupported;

    // Required by EF Core for materialization.
    private MessageTemplate()
    {
    }

    public static MessageTemplate Create(
        Guid tenantId, Guid channelId, string? name, string? language, TemplateCategory category, TemplateStatus status, string? bodyText, bool supported,
        DateTime syncedAt)
    {
        var template = new MessageTemplate
        {
            TenantId = TextRules.RequiredId(tenantId, "El identificador de la empresa es obligatorio."),
            ChannelId = TextRules.RequiredId(channelId, "El canal es obligatorio."),
            Name = IsValidName(name) ? name! : throw new DomainException(NameRuleMessage),
            Language = IsValidLanguage(language) ? language! : throw new DomainException(LanguageRuleMessage),
        };
        template.Refresh(category, status, bodyText, supported, syncedAt);
        return template;
    }

    /// <summary>What the provider says about the template now (a sync): everything but its identity.</summary>
    /// <param name="listedAt">
    /// When the provider's list was read. A status notification newer than that wins over the list's status (the list may be older than
    /// the notification); null applies the list's status.
    /// </param>
    public void Refresh(TemplateCategory category, TemplateStatus status, string? bodyText, bool supported, DateTime syncedAt, DateTime? listedAt = null)
    {
        var body = bodyText is null ? null : bodyText.Length <= BodyMaxLength && !bodyText.Contains('\0') ? bodyText : null;
        var names = body is null ? [] : TemplatePlaceholders.Parse(body);
        var tooMany = names.Count > MaxParameters;

        Category = Enum.IsDefined(category) ? category : TemplateCategory.Unknown;
        if (listedAt is null || StatusChangedAt is not { } notified || notified <= UtcTime.Normalize(listedAt.Value))
        {
            Status = Enum.IsDefined(status) ? status : TemplateStatus.Unknown;
        }

        BodyText = body;
        ParameterNames = tooMany ? [] : [.. names];
        IsSupported = supported && !tooMany && (bodyText is null || body is not null);
        SyncedAt = UtcTime.Normalize(syncedAt);
    }

    /// <summary>A status notification of the provider: only the status changes; the next sync brings the rest.</summary>
    /// <returns>False, and nothing changes, for a notification older than the last one applied (notifications can arrive out of order).</returns>
    public bool ChangeStatus(TemplateStatus status, DateTime changedAt)
    {
        var at = UtcTime.Normalize(changedAt);
        if (StatusChangedAt is { } last && at < last)
        {
            return false;
        }

        Status = Enum.IsDefined(status) ? status : TemplateStatus.Unknown;
        StatusChangedAt = at;
        return true;
    }

    /// <summary>
    /// The values for a send, checked and trimmed: exactly one per placeholder, each 1 to <see cref="ParameterMaxLength"/> characters without
    /// line breaks, tabs or more than four spaces in a row (WhatsApp refuses those in a parameter).
    /// </summary>
    public IReadOnlyList<string> ValidateParameters(IReadOnlyList<string?> values)
    {
        if (values.Count != ParameterNames.Length)
        {
            throw new DomainException(ParameterCountRuleMessage);
        }

        var result = new List<string>(values.Count);
        foreach (var raw in values)
        {
            var value = raw?.Trim();
            if (string.IsNullOrEmpty(value) || value.Length > ParameterMaxLength || value.Contains('\0') || value.Contains('\n') || value.Contains('\r')
                || value.Contains('\t') || value.Contains("     ", StringComparison.Ordinal))
            {
                throw new DomainException(ParameterRuleMessage);
            }

            result.Add(value);
        }

        return result;
    }

    /// <summary>The body as the customer receives it, with each placeholder replaced by its value (values are validated first).</summary>
    public string Render(IReadOnlyList<string> values)
    {
        if (BodyText is null)
        {
            return string.Empty;
        }

        var byName = ParameterNames.Select((name, i) => (name, i)).ToDictionary(p => p.name, p => values[p.i], StringComparer.Ordinal);

        // One pass over the body: a value that looks like a placeholder is text, never replaced again.
        return TemplatePlaceholders.Pattern().Replace(BodyText, match => byName.TryGetValue(match.Groups[1].Value, out var value) ? value : match.Value);
    }

    public static bool IsValidName(string? name)
        => name is { Length: >= 1 and <= NameMaxLength } && name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_');

    public static bool IsValidLanguage(string? language)
        => language is { Length: >= 2 and <= LanguageMaxLength } && LanguagePattern().IsMatch(language);

    private static Regex LanguagePattern() => new("^[a-z]{2,3}(_[A-Za-z0-9]{2,8})?$", RegexOptions.CultureInvariant);
}

/// <summary>The placeholders of a template text: <c>{{1}}</c> (positional) or <c>{{first_name}}</c> (named, lower case and '_').</summary>
public static partial class TemplatePlaceholders
{
    public static IReadOnlyList<string> Parse(string text)
    {
        var names = new List<string>();
        foreach (Match match in Pattern().Matches(text))
        {
            var name = match.Groups[1].Value;
            if (!names.Contains(name, StringComparer.Ordinal))
            {
                names.Add(name);
            }
        }

        return names.Count > 0 && names.All(IsPositional)
            ? [.. names.OrderBy(n => n.Length).ThenBy(n => n, StringComparer.Ordinal)]
            : names;
    }

    [GeneratedRegex(@"\{\{\s*([a-z0-9_]{1,64})\s*\}\}", RegexOptions.CultureInvariant)]
    internal static partial Regex Pattern();

    public static bool IsPositional(string name) => name.Length > 0 && name.All(char.IsAsciiDigit);
}

/// <summary>The template a message was sent from: its name and category (what Meta charges by). No parameter values: those are in the text.</summary>
public sealed record MessageTemplateUse(string Name, TemplateCategory Category);
