using Conversia.Application.DTOs.Messages;
using Conversia.Domain.Enums;

namespace Conversia.Application.Messages;

/// <summary>Turns the counts a processed delivery recorded into the one sentence a person reads: what became of the delivery.</summary>
public static class WebhookOutcomes
{
    public static (string Code, string Label) Describe(WebhookEventItem item) => Describe(
        Enum.Parse<WebhookEventStatus>(item.Status), item.LastError is not null, item.SkippedMessageCount, item.StatusCount, item.StoredMessageCount, item.DuplicateMessageCount);

    public static (string Code, string Label) Describe(
        WebhookEventStatus status, bool hasError, int? skipped, int? statuses, int? stored, int? duplicates)
    {
        switch (status)
        {
            case WebhookEventStatus.Failed:
                return ("failed", "Falló");
            case WebhookEventStatus.Pending:
                return hasError ? ("retrying", "Pendiente de reintento") : ("pending", "Pendiente de procesar");
        }

        // Deliveries processed before the stored count was recorded only know what was skipped and how many receipts they held.
        if (stored is null)
        {
            return skipped > 0 ? ("skipped", "Mensajes omitidos") : ("processed", "Procesada");
        }

        if (stored > 0)
        {
            return skipped > 0 ? ("partial", "Mensajes guardados, algunos omitidos") : ("stored", "Mensajes guardados");
        }

        if (skipped > 0)
        {
            return ("skipped", "Mensajes omitidos");
        }

        if (duplicates > 0)
        {
            return ("repeated", "Mensajes repetidos");
        }

        return statuses > 0 ? ("receipts", "Solo recibos de entrega") : ("empty", "Sin mensajes que guardar");
    }
}
