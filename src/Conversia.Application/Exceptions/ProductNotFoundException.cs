namespace Conversia.Application.Exceptions;

/// <summary>
/// Thrown when a product does not exist, or exists in another tenant. The API layer maps this to 404,
/// the same answer for both, so a cross-tenant probe learns nothing.
/// </summary>
public class ProductNotFoundException : Exception
{
    public const string DefaultMessage = "Producto no encontrado.";

    public ProductNotFoundException()
        : base(DefaultMessage)
    {
    }
}
