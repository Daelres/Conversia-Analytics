namespace Conversia.Web.Authentication;

public static class LocalReturnUrl
{
    private const int MaxLength = 2048;

    /// <summary>The URL when it is a path on this site, else "/". Browsers drop tabs and newlines, so any control character disqualifies it.</summary>
    public static string OrHome(string? returnUrl)
        => IsLocal(returnUrl) ? returnUrl! : "/";

    private static bool IsLocal(string? url)
        => url is { Length: > 0 and <= MaxLength }
           && url[0] == '/'
           && (url.Length == 1 || (url[1] != '/' && url[1] != '\\'))
           && !url.Any(char.IsControl);
}
