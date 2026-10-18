namespace Conversia.Web.Authentication;

public static class LocalReturnUrl
{
    private const int MaxLength = 2048;

    /// <summary>Where a person lands after signing in or registering when no page sent them: the executive dashboard.</summary>
    public const string Landing = "/panel";

    /// <summary>The URL when it is a path on this site, else the landing page. Browsers drop tabs and newlines, so any control character disqualifies it.</summary>
    public static string OrHome(string? returnUrl)
        => IsLocal(returnUrl) ? returnUrl! : Landing;

    private static bool IsLocal(string? url)
        => url is { Length: > 0 and <= MaxLength }
           && url[0] == '/'
           && (url.Length == 1 || (url[1] != '/' && url[1] != '\\'))
           && !url.Any(char.IsControl);
}
