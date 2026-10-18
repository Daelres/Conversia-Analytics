using Conversia.Web.Authentication;
using Conversia.Web.Services;
using Microsoft.AspNetCore.Components;

namespace Conversia.Web.Components.Pages;

/// <summary>Shared plumbing of the pages that call the API during static server-side rendering.</summary>
public abstract class ApiPageBase : ComponentBase
{
    [CascadingParameter]
    protected HttpContext HttpContext { get; set; } = default!;

    [Inject]
    protected IApiClient Api { get; set; } = default!;

    [Inject]
    protected NavigationManager Navigation { get; set; } = default!;

    /// <summary>Hands the profile a page has already loaded to the shell, so the account card needs no API call of its own.</summary>
    [Inject]
    protected ShellState Shell { get; set; } = default!;

    /// <summary>True once the response has become a redirect; the page then renders nothing.</summary>
    protected bool Redirected { get; private set; }

    protected void SetStatusCode(int statusCode) => HttpContext.Response.StatusCode = statusCode;

    protected void RedirectTo(string uri)
    {
        Redirected = true;
        Navigation.NavigateTo(uri);
    }

    /// <summary>The API refused the stored token (expired or revoked): drop the cookie and start over at the login page.</summary>
    protected async Task EndSessionAsync()
    {
        await HttpContext.SignOutApiUserAsync();
        RedirectTo(AuthenticationExtensions.LoginPath);
    }
}
