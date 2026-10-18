using Conversia.Application.DTOs.Products;
using Conversia.Domain.Enums;
using Conversia.Web.Http;
using Conversia.Web.Services;

namespace Conversia.Web.Components.Pages;

/// <summary>What the product pages share on top of <see cref="AuthenticatedPageBase"/>: the product, and the caller's right to change it.</summary>
public abstract class ProductPageBase : AuthenticatedPageBase
{
    /// <summary>The product does not exist in the caller's tenant; the page then says so instead of showing its content.</summary>
    protected bool ProductMissing { get; private set; }

    /// <summary>The product of the page, once <see cref="LoadProductAsync"/> has found it.</summary>
    protected ProductResponse? Product { get; private set; }

    /// <summary>Whether the caller may change products; only an offer, the API stays the authority.</summary>
    protected bool CanManage { get; private set; }

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

    protected override void OnNotFound() => ShowProductNotFound();

    protected void ShowProductNotFound()
    {
        ProductMissing = true;
        ProductNotFoundMarker.Mark(HttpContext);
        SetStatusCode(StatusCodes.Status404NotFound);
        Message = UiMessages.ProductNotFound;
    }
}
