namespace Conversia.Application.Exceptions;

/// <summary>Thrown when the tenant already holds a product with the SKU. The API layer maps this to 409.</summary>
public class DuplicateSkuException : Exception
{
    public const string DefaultMessage = "Ya existe un producto con ese SKU.";

    public DuplicateSkuException()
        : base(DefaultMessage)
    {
    }
}
