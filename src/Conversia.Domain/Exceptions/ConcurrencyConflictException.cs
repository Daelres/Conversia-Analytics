namespace Conversia.Domain.Exceptions;

/// <summary>A row changed between the moment it was read and the moment it was written (optimistic concurrency). The API layer maps it to 409.</summary>
public class ConcurrencyConflictException : Exception
{
    public const string DefaultMessage = "El registro cambió mientras lo editabas. Vuelve a cargar la página e inténtalo de nuevo.";

    public ConcurrencyConflictException(Exception? innerException = null)
        : base(DefaultMessage, innerException)
    {
    }
}
