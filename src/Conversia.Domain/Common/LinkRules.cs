using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Conversia.Domain.Common;

/// <summary>
/// Finds links in a text the assistant may send (docs/ASSISTANT.md, "Knowledge base"): any <c>scheme://</c>, markdown links, and bare domains on
/// any alphabetic top-level domain (<c>rb.gy/x</c>, <c>x.dev</c>). A link whose authority carries credentials, backslashes or percent-encoded
/// characters is never valid. Hosts are compared in their ASCII (punycode) form, so a look-alike written in another script never matches. The
/// assistant's texts allow no link at all, or only to the company's one allowed booking/payment domain and its subdomains. Also cleans a free
/// text of control and invisible characters.
/// </summary>
public static partial class LinkRules
{
    public const int DomainMaxLength = 253;

    /// <summary>What a link that can never be valid is reported as (it matches no allowed domain).</summary>
    private const string Invalid = "\u0000invalid";

    /// <summary>Public suffixes, link shorteners and hosts anyone can publish under: never a company's allowed domain.</summary>
    private static readonly HashSet<string> Shared = new(StringComparer.Ordinal)
    {
        "co.uk", "com.co", "net.co", "org.co", "edu.co", "gov.co", "mil.co", "nom.co", "com.mx", "com.ar", "com.br", "com.pe", "com.ec", "com.ve",
        "com.es", "co.za", "com.au", "linktr.ee", "bit.ly", "tinyurl.com", "t.co", "goo.gl", "rb.gy", "is.gd", "cutt.ly", "ow.ly", "buff.ly",
        "shorturl.at", "tiny.cc", "s.id", "wa.me", "wa.link", "api.whatsapp.com", "github.io", "gitlab.io", "blogspot.com", "wordpress.com",
        "wixsite.com", "weebly.com", "netlify.app", "vercel.app", "herokuapp.com", "web.app", "firebaseapp.com", "azurewebsites.net",
        "pages.dev", "glitch.me", "sites.google.com", "forms.gle", "docs.google.com", "drive.google.com", "carrd.co", "notion.site",
    };

    /// <summary>The hosts linked in <paramref name="text"/>, ASCII lower case (a link that can never be valid is reported as such).</summary>
    public static IReadOnlyList<string> Hosts(string text)
    {
        var hosts = new List<string>();
        if (MarkdownLink().IsMatch(text))
        {
            hosts.Add(Invalid);
        }

        foreach (Match match in SchemeLink().Matches(text))
        {
            hosts.Add(HostOf(match.Groups["authority"].Value) ?? Invalid);
        }

        foreach (Match match in BareDomain().Matches(text))
        {
            hosts.Add(AsciiDomain(match.Groups["host"].Value) ?? Invalid);
        }

        return hosts;
    }

    public static bool HasLink(string text) => Hosts(text).Count > 0;

    /// <summary>Every link of <paramref name="text"/> goes to <paramref name="allowedDomain"/> (ASCII form) or one of its subdomains (none when it is null).</summary>
    public static bool OnlyLinksTo(string text, string? allowedDomain)
        => Hosts(text).All(host => allowedDomain is not null && (host == allowedDomain || host.EndsWith("." + allowedDomain, StringComparison.Ordinal)));

    /// <summary>The ASCII (punycode) form of a host name, lower case, without a trailing dot; null when it is not a valid name.</summary>
    public static string? AsciiDomain(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
        {
            return null;
        }

        try
        {
            var ascii = new IdnMapping().GetAscii(domain.Trim().TrimEnd('.')).ToLowerInvariant();
            return ascii.StartsWith("www.", StringComparison.Ordinal) ? ascii[4..] : ascii;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>A host name: labels of letters, digits and hyphens, at least one dot, no scheme, path or port (ASCII form).</summary>
    public static bool IsDomain(string? domain) => domain is { Length: > 0 and <= DomainMaxLength } && Domain().IsMatch(domain);

    /// <summary>A domain a company may allow: a valid host name that is not a public suffix, a link shortener or a shared host (or under one).</summary>
    public static bool IsAllowableDomain(string? domain)
    {
        var ascii = AsciiDomain(domain);
        return IsDomain(ascii) && !Shared.Any(shared => ascii == shared || ascii!.EndsWith("." + shared, StringComparison.Ordinal) && IsSharedHost(shared));
    }

    // A company may own example.com.co (under the public suffix com.co) but not anything under a shared host (x.github.io).
    private static bool IsSharedHost(string shared) => shared.Count(c => c == '.') >= 1 && !PublicSuffix().IsMatch(shared);

    /// <summary>Drops control, format and zero-width characters, turns tabs and line breaks into spaces, collapses runs of spaces and trims.</summary>
    public static string Clean(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                builder.Append(' ');
            }
            else if (!char.IsControl(c) && CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.Format)
            {
                builder.Append(c);
            }
        }

        return Spaces().Replace(builder.ToString(), " ").Trim();
    }

    /// <summary>Like <see cref="Clean"/> but keeps line breaks (a multi-line answer).</summary>
    public static string CleanMultiline(string text)
        => string.Join('\n', text.Replace("\r\n", "\n").Split('\n').Select(Clean)).Trim('\n');

    // The authority of a scheme link: credentials (@), backslashes and percent-encoding make it ambiguous between parsers, so it is never valid.
    private static string? HostOf(string authority)
    {
        if (authority.Length == 0 || authority.IndexOfAny(['@', '\\', '%']) >= 0)
        {
            return null;
        }

        var colon = authority.LastIndexOf(':');
        var host = colon >= 0 ? authority[..colon] : authority;
        if (colon >= 0 && (colon == authority.Length - 1 || !authority[(colon + 1)..].All(char.IsAsciiDigit)))
        {
            return null;
        }

        var ascii = AsciiDomain(host);
        return IsDomain(ascii) ? ascii : null;
    }

    [GeneratedRegex(@"\[[^\]\n]*\]\([^)\n]*\)")]
    private static partial Regex MarkdownLink();

    [GeneratedRegex(@"[A-Za-z][A-Za-z0-9+.\-]*://(?<authority>[^\s/?#]*)")]
    private static partial Regex SchemeLink();

    [GeneratedRegex(@"(?<![\p{L}\p{N}@._\-])(?<host>(?:[\p{L}\p{N}](?:[\p{L}\p{N}\-]*[\p{L}\p{N}])?\.)+\p{L}{2,63})(?![\p{L}\p{N}@\-])")]
    private static partial Regex BareDomain();

    [GeneratedRegex(@"^(?=.{1,253}$)(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+(?:[a-z]{2,63}|xn--[a-z0-9-]{1,59})$")]
    private static partial Regex Domain();

    // Two-label public suffixes (com.co, co.uk...): a company's own domain may sit under them.
    [GeneratedRegex(@"^(com|net|org|edu|gov|mil|nom|co)\.[a-z]{2}$")]
    private static partial Regex PublicSuffix();

    [GeneratedRegex(" {2,}")]
    private static partial Regex Spaces();
}
