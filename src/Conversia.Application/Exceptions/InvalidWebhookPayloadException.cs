namespace Conversia.Application.Exceptions;

/// <summary>The webhook body cannot be stored as <c>jsonb</c> (for example a <c>\u0000</c> escape or an unpaired surrogate). The webhook answers 400.</summary>
public class InvalidWebhookPayloadException : Exception
{
    public const string DefaultMessage = "El cuerpo del webhook no es un JSON válido.";

    public InvalidWebhookPayloadException()
        : base(DefaultMessage)
    {
    }
}
