using Conversia.Domain.Exceptions;

namespace Conversia.Domain.Common;

/// <summary>
/// The one definition of the free-text rule shared by every entity and by the request validation: the trimmed value is within
/// its length, holds no U+0000 (PostgreSQL text cannot store it) and no unpaired UTF-16 surrogate (Npgsql refuses to write it).
/// </summary>
public static class TextRules
{
    /// <summary>The wording of the rule, the part after the subject ("El nombre ...").</summary>
    public static string RuleText(int maxLength, bool required = true)
        => (required ? "es obligatorio, " : "es opcional, ")
           + $"no puede superar los {maxLength} caracteres ni contener caracteres nulos (se ignoran los espacios al principio y al final)";

    /// <summary>True when the value, once trimmed, is not empty, at most <paramref name="maxLength"/> long and free of U+0000 and unpaired surrogates.</summary>
    public static bool IsValidRequired(string? value, int maxLength)
    {
        var trimmed = value?.Trim();
        return !string.IsNullOrEmpty(trimmed) && trimmed.Length <= maxLength && !trimmed.Contains('\0') && !HasUnpairedSurrogate(trimmed);
    }

    /// <summary>True when the text holds a high surrogate without its low one, or a low one without its high one.</summary>
    public static bool HasUnpairedSurrogate(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]))
            {
                if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1]))
                {
                    return true;
                }

                i++;
            }
            else if (char.IsLowSurrogate(text[i]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Like <see cref="IsValidRequired"/>, but null or blank is fine too.</summary>
    public static bool IsValidOptional(string? value, int maxLength)
        => string.IsNullOrWhiteSpace(value) ? !(value?.Contains('\0') ?? false) : IsValidRequired(value, maxLength);

    public static string Required(string? value, int maxLength, string message)
        => IsValidRequired(value, maxLength) ? value!.Trim() : throw new DomainException(message);

    public static string? Optional(string? value, int maxLength, string message)
    {
        if (!IsValidOptional(value, maxLength))
        {
            throw new DomainException(message);
        }

        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>
    /// Cuts <paramref name="text"/> to at most <paramref name="maxLength"/> UTF-16 units without splitting a surrogate pair: a lone
    /// surrogate would make the whole value unwritable (Npgsql refuses it), and with it the delivery that carried it.
    /// </summary>
    public static string Truncate(string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            return text;
        }

        var end = maxLength;
        if (end > 0 && char.IsHighSurrogate(text[end - 1]))
        {
            end--;
        }

        return text[..end];
    }

    public static Guid RequiredId(Guid id, string message)
        => id == Guid.Empty ? throw new DomainException(message) : id;
}
