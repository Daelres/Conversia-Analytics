using Conversia.Web.Authentication;
using Conversia.Web.Http;

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
        NotFoundInPlace();
        Message = null;
    }

    /// <summary>
    /// A 404 the page answers itself. The status-code pages re-run every 404 into the public "page not found"; the marker makes that page (and
    /// the layout around it) the administration's own "company not found".
    /// </summary>
    protected void NotFoundInPlace()
    {
        NotFoundMarker.Mark(HttpContext, NotFoundMarker.Company);
        SetStatusCode(StatusCodes.Status404NotFound);
    }
}
