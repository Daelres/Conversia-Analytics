using Conversia.Web.Authentication;
using Conversia.Web.Services;

namespace Conversia.Web.Components.Pages;

/// <summary>
/// Plumbing shared by the pages of a signed-in person: the caller's token, one place that turns a failed API call into a status and a
/// message, and the "denied" state (the API refused the caller, so the page shows no data and no controls).
/// </summary>
public abstract class AuthenticatedPageBase : ApiPageBase
{
    protected string? Token { get; private set; }

    /// <summary>The error to show above the page's content.</summary>
    protected string? Message { get; set; }

    /// <summary>The API refused the caller; the page then shows no data and no controls.</summary>
    protected bool Denied { get; private set; }

    protected bool IsPost => HttpMethods.IsPost(HttpContext.Request.Method);

    /// <summary>False when there is no token to call the API with; the session has then been ended.</summary>
    protected async Task<bool> TryReadTokenAsync()
    {
        Token = await HttpContext.GetApiTokenAsync();
        if (Token is not null)
        {
            return true;
        }

        await EndSessionAsync();
        return false;
    }

    /// <summary>
    /// Reacts to the first failed outcome (an expired token outranks the others) and returns true; false when all succeeded.
    /// A 404 is for <see cref="OnNotFound"/>: what is missing is for the page to say.
    /// </summary>
    protected async Task<bool> TryFailAsync(params IApiOutcome[] outcomes)
    {
        var failed = outcomes.FirstOrDefault(o => o.Status == ApiResultStatus.Unauthorized)
                     ?? outcomes.FirstOrDefault(o => o.Status == ApiResultStatus.TenantNotActive)
                     ?? outcomes.FirstOrDefault(o => o.Status != ApiResultStatus.Success);
        if (failed is null)
        {
            return false;
        }

        switch (failed.Status)
        {
            case ApiResultStatus.Unauthorized:
                await EndSessionAsync();
                break;
            case ApiResultStatus.TenantNotActive:
                // An expected state, not an error: every page of a company that cannot operate yet ends up here, in one place.
                RedirectTo(LocalReturnUrl.PendingAccount);
                break;
            case ApiResultStatus.Forbidden:
                Deny();
                break;
            case ApiResultStatus.NotFound:
                OnNotFound();
                break;
            case ApiResultStatus.ValidationFailed:
                SetStatusCode(StatusCodes.Status400BadRequest);
                Message = UiMessages.InvalidRequest;
                break;
            case ApiResultStatus.RateLimited:
                Message = UiMessages.TooManyAttempts(failed.RetryAfterSeconds);
                break;
            default:
                SetStatusCode(StatusCodes.Status503ServiceUnavailable);
                Message = UiMessages.Unavailable;
                break;
        }

        return true;
    }

    /// <summary>The page's own wording of "that does not exist"; the generic one is a plain 404.</summary>
    protected virtual void OnNotFound()
    {
        SetStatusCode(StatusCodes.Status404NotFound);
        Message = UiMessages.NotFound;
    }

    protected void Deny()
    {
        Denied = true;
        SetStatusCode(StatusCodes.Status403Forbidden);
        Message = UiMessages.NotAuthorized;
    }
}
