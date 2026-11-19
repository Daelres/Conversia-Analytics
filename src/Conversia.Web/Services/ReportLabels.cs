using Conversia.Application.DTOs.Reports;

namespace Conversia.Web.Services;

/// <summary>What a person reads for the codes of the manager reports (docs/REPORTS.md). The codes stay in <c>data-*</c> hooks.</summary>
public static class ReportLabels
{
    public static readonly IReadOnlyList<(int Day, string Label)> Days =
        [(1, "Lunes"), (2, "Martes"), (3, "Miércoles"), (4, "Jueves"), (5, "Viernes"), (6, "Sábado"), (7, "Domingo")];

    public static readonly IReadOnlyList<(string Code, string Label)> Frequencies =
        [("Daily", "Diario (el día anterior)"), ("Weekly", "Semanal (la semana anterior)"), ("Monthly", "Mensual (el mes anterior)")];

    public static string Frequency(string? frequency) => frequency switch
    {
        "Daily" => "Diario",
        "Weekly" => "Semanal",
        "Monthly" => "Mensual",
        _ => "—",
    };

    public static string Hour(int hour) => $"{hour:00}:00";

    /// <summary>"Semanal, los lunes a las 07:00".</summary>
    public static string Schedule(ReportDefinitionResponse definition) => definition.Frequency switch
    {
        "Daily" => $"Diario, a las {Hour(definition.SendHour)}",
        "Weekly" => $"Semanal, los {Days.First(d => d.Day == definition.SendDayOfWeek).Label.ToLowerInvariant()} a las {Hour(definition.SendHour)}",
        "Monthly" => $"Mensual, el día 1 a las {Hour(definition.SendHour)}",
        _ => "—",
    };

    public static string Generator(string? generator) => generator switch
    {
        "Llm" => "Redactado con IA",
        "Template" => "Redactado automáticamente",
        _ => "—",
    };

    public static string Channel(string? channel) => channel switch
    {
        "Email" => "Correo",
        "WhatsApp" => "WhatsApp",
        _ => "—",
    };

    public static string Status(string? status) => status switch
    {
        "Sent" => "Enviado",
        "Pending" => "Pendiente",
        "Failed" => "No se pudo enviar",
        "Skipped" => "No se envió",
        _ => "—",
    };

    public static string Problem(ReportDeliveryResponse delivery) => Problem(delivery.ErrorCode);

    /// <summary>Why a delivery was not sent, in words a manager understands.</summary>
    public static string Problem(string? code) => code switch
    {
        null => string.Empty,
        "unsubscribed" => "La persona se dio de baja de los correos de este informe.",
        "no_opt_in" => "La persona no ha aceptado recibir informes por WhatsApp.",
        "recipient_removed" => "La persona ya no recibe este informe.",
        "email_not_configured" => "El envío de correos no está configurado en la plataforma.",
        "template_unavailable" => "La plantilla de WhatsApp ya no está aprobada o no existe.",
        "channel_unavailable" => "El número de WhatsApp está desactivado.",
        "credentials_missing" or "provider_credentials_invalid" => "El número de WhatsApp no tiene una clave válida de 360dialog.",
        "recipient_rejected" or "recipient_invalid" => "El servidor de correo rechazó la dirección.",
        "provider_unreachable" => "WhatsApp no respondió; no se reintenta para evitar mensajes repetidos.",
        "provider_limit_reached" => "Se alcanzó el límite de envíos; se reintenta más tarde.",
        "smtp_unavailable" or "smtp_temporary" or "smtp_tls" or "smtp_authentication" => "El servidor de correo no respondió; se reintenta más tarde.",
        _ => "No se pudo enviar.",
    };
}
