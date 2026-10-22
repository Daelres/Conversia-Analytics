namespace Conversia.Web.Services;

/// <summary>
/// The optional <c>Support:ContactText</c>: how a person whose company is pending or suspended reaches the team (for example "Escríbenos a
/// soporte@empresa.co o al +57 300 000 0000"). Plain text, shown HTML-encoded on the pending page; nothing in it is interpreted.
/// </summary>
public sealed class SupportOptions
{
    public const string ContactTextKey = "Support:ContactText";
    public const int MaxContactTextLength = 300;

    /// <summary>Null when not configured: the page then shows no contact card.</summary>
    public string? ContactText { get; init; }

    /// <summary>
    /// Reads and validates the setting. Blank means "none". Longer than <see cref="MaxContactTextLength"/> characters or holding a control
    /// character (a line break included: it is one line of text) stops the start; the message names the key and never repeats the value.
    /// </summary>
    public static SupportOptions FromConfiguration(IConfiguration configuration)
    {
        var text = configuration[ContactTextKey]?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return new SupportOptions();
        }

        if (text.Length > MaxContactTextLength)
        {
            throw new InvalidOperationException($"{ContactTextKey} must be at most {MaxContactTextLength} characters long.");
        }

        if (text.Any(char.IsControl))
        {
            throw new InvalidOperationException($"{ContactTextKey} must be one line of plain text, without control characters.");
        }

        return new SupportOptions { ContactText = text };
    }
}
