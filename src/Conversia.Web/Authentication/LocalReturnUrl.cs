namespace Conversia.Web.Authentication;

public static class LocalReturnUrl
{
    private const int MaxLength = 2048;

    /// <summary>Where a person lands after signing in or registering when no page sent them: the executive dashboard.</summary>
    public const string Landing = "/panel";

    /// <summary>Where a company that cannot operate yet (pending or suspended) lands instead: the page that says where it stands.</summary>
    public const string PendingAccount = "/cuenta-pendiente";

    /// <summary>The query that marks the first visit after registering, so the page can say the company "quedó registrada".</summary>
    public const string JustRegistered = PendingAccount + "?registrado=1";

    /// <summary>Where the administrator lands after signing in when no page sent them.</summary>
    public const string AdminLanding = "/admin";

    /// <summary>The URL when it is a path inside the administration area (<c>/admin</c> or below), else the administrator's landing page.</summary>
    public static string OrAdminHome(string? returnUrl)
        => IsLocal(returnUrl) && IsAdminPath(returnUrl!) ? returnUrl! : AdminLanding;

    private static bool IsAdminPath(string url)
    {
        var path = url.Split('?', '#')[0];
        return path.Equals("/admin", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/admin/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The URL when it is a path on this site, else the landing page. Browsers drop tabs and newlines, so any control character disqualifies it.</summary>
    public static string OrHome(string? returnUrl)
        => IsLocal(returnUrl) ? returnUrl! : Landing;

    private static bool IsLocal(string? url)
        => url is { Length: > 0 and <= MaxLength }
           && url[0] == '/'
           && (url.Length == 1 || (url[1] != '/' && url[1] != '\\'))
           && !url.Any(char.IsControl);
}
