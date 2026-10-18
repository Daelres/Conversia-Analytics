namespace Conversia.Application.Exceptions;

/// <summary>Thrown when an update used a stale row version. The API layer maps this to 409.</summary>
public class ProductConcurrencyException : Exception
{
    public const string DefaultMessage = "El producto fue modificado por otra persona. Recarga e inténtalo de nuevo.";

    public ProductConcurrencyException()
        : base(DefaultMessage)
    {
    }
}
