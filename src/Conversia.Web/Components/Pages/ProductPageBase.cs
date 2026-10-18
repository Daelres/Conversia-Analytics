using Conversia.Application.DTOs.Products;
using Conversia.Domain.Enums;
using Conversia.Web.Authentication;
using Conversia.Web.Http;
using Conversia.Web.Services;


namespace Conversia.Web.Components.Pages;

/// <summary>Plumbing shared by the product pages: the caller's token, and one place that turns a failed API call into a status and a message.</summary>
public abstract class ProductPageBase : ApiPageBase
{
    protected string? Token { get; private set; }

    /// <summary>The error to show above the page's content.</summary>
    protected string? Message { get; set; }

    /// <summary>The API refused the caller; the page then shows no data and no controls.</summary>
    protected bool Denied { get; private set; }

    /// <summary>The product does not exist in the caller's tenant; the page then says so instead of showing its content.</summary>
    protected bool ProductMissing { get; private set; }

    /// <summary>The product of the page, once <see cref="LoadProductAsync"/> has found it.</summary>
    protected ProductResponse? Product { get; private set; }

    /// <summary>Whether the caller may change products; only an offer, the API stays the authority.</summary>
    protected bool CanManage { get; private set; }

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

    /// <summary>The product and the caller's rights in one parallel round; a caller who may not manage is denied.</summary>
    protected async Task LoadProductAsync(Guid id)
    {
        var productTask = Api.GetProductAsync(Token!, id);
        var profileTask = Api.GetMyProfileAsync(Token!);
        await Task.WhenAll(productTask, profileTask);

        if (await TryFailAsync(productTask.Result, profileTask.Result))
        {
            return;
        }

        Product = productTask.Result.Value;
        Shell.Remember(profileTask.Result.Value!);
        CanManage = profileTask.Result.Value!.Can(Permission.ProductsManage);
        if (!CanManage)
        {
            Deny();
        }
    }

    /// <summary>
    /// Reacts to the first failed outcome (an expired token outranks the others) and returns true; false when all succeeded.
    /// A 404 is not handled here: what is missing is for the page to say.
    /// </summary>
    protected async Task<bool> TryFailAsync(params IApiOutcome[] outcomes)
    {
        var failed = outcomes.FirstOrDefault(o => o.Status == ApiResultStatus.Unauthorized)
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
            case ApiResultStatus.Forbidden:
                Deny();
                break;
            case ApiResultStatus.NotFound:
                ShowProductNotFound();
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

    protected void Deny()
    {
        Denied = true;
        SetStatusCode(StatusCodes.Status403Forbidden);
        Message = UiMessages.NotAuthorized;
    }

    protected void ShowProductNotFound()
    {
        ProductMissing = true;
        ProductNotFoundMarker.Mark(HttpContext);
        SetStatusCode(StatusCodes.Status404NotFound);
        Message = UiMessages.ProductNotFound;
    }
}
