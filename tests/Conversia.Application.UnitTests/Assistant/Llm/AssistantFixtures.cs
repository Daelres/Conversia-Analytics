using Conversia.Application.Assistant;
using Conversia.Application.Classification;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;

namespace Conversia.Application.UnitTests.Assistant.Llm;

/// <summary>A synthetic laser hair removal company (the pilot's domain) and helpers to build conversations. No real data.</summary>
internal static class AssistantFixtures
{
    public static readonly Guid BranchId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid OtherBranch = Guid.Parse("22222222-2222-2222-2222-222222222222");

    /// <summary>Thursday 19 November 2026, 15:40 in Bogota (20:40 UTC).</summary>
    public static readonly DateTime UtcNow = new(2026, 11, 19, 20, 40, 0, DateTimeKind.Utc);

    public static readonly TimeZoneInfo Bogota = Conversia.Application.Classification.Llm.AiQuotaSettings.BogotaTimeZone();

    public static DateTime LocalNow => TimeZoneInfo.ConvertTimeFromUtc(UtcNow, Bogota);

    public static AssistantKnowledge Laser() => new()
    {
        Services =
        [
            new KnowledgeService("Depilación láser de axilas", "Sesión con láser de diodo, casi sin dolor.", 80_000, null, 20, "Se recomiendan 6 sesiones.", true, null),
            new KnowledgeService("Depilación láser de piernas completas", "Incluye muslos y pantorrillas.", 180_000, 220_000, 50, null, true, null),
            new KnowledgeService("Depilación láser de bikini", null, 90_000, null, 25, null, true, [BranchId]),
            new KnowledgeService("Depilación de espalda", null, 150_000, null, 40, null, false, null),
            new KnowledgeService("Depilación láser de rostro", null, 70_000, null, 15, null, true, [OtherBranch]),
        ],
        Promotions =
        [
            new KnowledgePromotion("Noviembre Suave", "20% de descuento en axilas pagando el paquete de 6 sesiones.", new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30), null),
            new KnowledgePromotion("Verano Express", "15% en piernas.", new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30), null),
        ],
        OpeningHours =
        [
            new KnowledgeOpeningHours(null, DayOfWeek.Monday, 8 * 60, 18 * 60),
            new KnowledgeOpeningHours(null, DayOfWeek.Tuesday, 8 * 60, 18 * 60),
            new KnowledgeOpeningHours(null, DayOfWeek.Wednesday, 8 * 60, 18 * 60),
            new KnowledgeOpeningHours(null, DayOfWeek.Thursday, 8 * 60, 18 * 60),
            new KnowledgeOpeningHours(null, DayOfWeek.Friday, 8 * 60, 18 * 60),
            new KnowledgeOpeningHours(null, DayOfWeek.Saturday, 9 * 60, 13 * 60),
        ],
        Holidays = [new KnowledgeHoliday(new DateOnly(2026, 12, 8), null, "Día de la Inmaculada Concepción")],
        BookingPolicy = new KnowledgeBookingPolicy(
            "Reserva en https://reservas.laserdemo.co/agenda o por este chat.", "Abono de 30.000 pesos para separar el cupo.", "Puedes cancelar sin costo hasta 24 horas antes."),
        PaymentMethods = ["Tarjeta de crédito", "Nequi", "Efectivo en la sede"],
        Faqs =
        [
            new KnowledgeFaq("¿Duele la depilación láser?", "Se siente un leve calor; el láser de diodo casi no duele.", "dolor"),
            new KnowledgeFaq("¿Cada cuánto son las sesiones?", "Cada 4 a 6 semanas.", "sesiones"),
        ],
        AllowedTopics = ["depilación láser", "citas", "precios", "promociones"],
        ForbiddenPhrases = ["garantizado al 100"],
        EscalationRules = ["Si piden factura electrónica"],
        AllowedLinkDomain = "laserdemo.co",
    };

    public static SnapshotMessage Customer(string text, int minute = 0)
        => new(MessageDirection.Inbound, "text", text, UtcNow.AddMinutes(-30 + minute), Guid.NewGuid());

    public static SnapshotMessage Business(string text, int minute = 0, MessageSender sender = MessageSender.AiAssistant)
        => new(MessageDirection.Outbound, "text", text, UtcNow.AddMinutes(-30 + minute), Guid.NewGuid(), sender);

    public static ConversationSnapshot Chat(params SnapshotMessage[] messages)
        => new(Guid.Parse("33333333-3333-3333-3333-333333333333"), null, UtcNow.AddHours(-1), UtcNow.AddHours(-1), null,
            messages.Select((m, i) => m with { SentAt = UtcNow.AddMinutes(-30 + i) }).ToList(), Guid.Parse("44444444-4444-4444-4444-444444444444"));

    public static AssistantReplySettings Settings(string? tone = null)
        => new("es-CO", tone, AssistantDisclosureMode.OnRequest, AssistantSettings.DefaultDisclosureText, 0, 8);

    public static AssistantReplyRequest Request(ConversationSnapshot chat, AssistantKnowledge? knowledge = null, string? firstName = null, int offTopic = 0, bool sandbox = true)
        => new(chat.TenantId, chat.ConversationId, sandbox, chat, Guid.NewGuid(), 3, Settings(), MustDisclose: false)
        {
            Knowledge = knowledge ?? Laser(),
            BranchId = AssistantFixtures.BranchId,
            CustomerFirstName = firstName,
            OffTopicCount = offTopic,
            UtcNow = UtcNow,
        };
}
