using Conversia.Web.Authentication;

namespace Conversia.Web.Components.Pages.Admin;

/// <summary>
/// The base of every administrator page. It is the company pages' plumbing (<see cref="AuthenticatedPageBase"/>: one place that turns a
/// failed API call into a status and a message) pointed at the administrator's own session: the token comes from the administrator's cookie,
/// and a token the API refuses ends that session, not a company one.
/// </summary>
public abstract class AdminPageBase : AuthenticatedPageBase
{
    protected override Task<string?> ReadStoredTokenAsync() => HttpContext.GetPlatformAdminTokenAsync();

    protected override async Task EndSessionAsync()
    {
        await HttpContext.SignOutPlatformAdminAsync();
        RedirectTo(AuthenticationExtensions.AdminLoginPath);
    }

    /// <summary>The page's own words for "that company does not exist"; a plain 404 with the generic message otherwise.</summary>
    protected bool CompanyMissing { get; private set; }

    protected override void OnNotFound()
    {
        CompanyMissing = true;
        SetStatusCode(StatusCodes.Status404NotFound);
        Message = null;
    }
}
