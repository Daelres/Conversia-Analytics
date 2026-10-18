namespace Conversia.Application.Exceptions;

/// <summary>
/// A metrics query exceeded <c>Metrics:QueryTimeoutSeconds</c> and was cancelled by the database. The message is for the caller (Spanish, no SQL, no
/// detail of the statement); the API answers it with a 503.
/// </summary>
public sealed class MetricsQueryTimeoutException : Exception
{
    public const string DefaultMessage = "La consulta tardó demasiado en responder. Acota el rango de fechas o los filtros e inténtalo de nuevo.";

    public MetricsQueryTimeoutException()
        : base(DefaultMessage)
    {
    }
}
