using Conversia.Application.DTOs.Messages;
using Conversia.Application.Interfaces;
using Conversia.Application.Messages;
using Conversia.Application.Services;
using Conversia.Application.Time;
using Conversia.Domain.Enums;
using Conversia.Tests.Shared;
using Moq;

namespace Conversia.Application.UnitTests.Messages;

public class MessageFeedTests
{
    [Theory]
    [InlineData("573001234567", "…4567")]
    [InlineData("+57 300 123 4567", "…4567")]
    [InlineData("1234", "…1234")]
    [InlineData("12", "…12")]
    [InlineData("", "…")]
    [InlineData(null, "…")]
    public void TheMaskKeepsTheLastFourDigitsOnly(string? waId, string expected)
        => Assert.Equal(expected, WhatsAppIdMask.Mask(waId));

    [Theory]
    [InlineData("text", "Texto")]
    [InlineData("IMAGE", "Imagen")]
    [InlineData("unknown_kind", "Mensaje (unknown_kind)")]
    [InlineData(null, "Mensaje")]
    public void TypesHaveSpanishLabels(string? type, string expected)
        => Assert.Equal(expected, MessageTypeLabels.For(type));

    [Theory]
    [InlineData(WebhookEventStatus.Failed, true, 0, 0, 0, 0, "failed", "Falló")]
    [InlineData(WebhookEventStatus.Pending, false, null, null, null, null, "pending", "Pendiente de procesar")]
    [InlineData(WebhookEventStatus.Pending, true, null, null, null, null, "retrying", "Pendiente de reintento")]
    [InlineData(WebhookEventStatus.Processed, false, 0, 0, 2, 0, "stored", "Mensajes guardados")]
    [InlineData(WebhookEventStatus.Processed, false, 1, 0, 2, 0, "partial", "Mensajes guardados, algunos omitidos")]
    [InlineData(WebhookEventStatus.Processed, false, 1, 0, 0, 0, "skipped", "Mensajes omitidos")]
    [InlineData(WebhookEventStatus.Processed, false, 0, 0, 0, 1, "repeated", "Mensajes repetidos")]
    [InlineData(WebhookEventStatus.Processed, false, 0, 3, 0, 0, "receipts", "Solo recibos de entrega")]
    [InlineData(WebhookEventStatus.Processed, false, 0, 0, 0, 0, "empty", "Sin mensajes que guardar")]
    [InlineData(WebhookEventStatus.Processed, false, 1, 0, null, null, "skipped", "Mensajes omitidos")]
    [InlineData(WebhookEventStatus.Processed, false, 0, 2, null, null, "processed", "Procesada")]
    public void TheOutcomeSaysWhatBecameOfTheDelivery(
        WebhookEventStatus status, bool hasError, int? skipped, int? statuses, int? stored, int? duplicates, string code, string label)
        => Assert.Equal((code, label), WebhookOutcomes.Describe(status, hasError, skipped, statuses, stored, duplicates));

    [Fact]
    public async Task TodayStartsAtLocalMidnightOfTheCompanyZone()
    {
        // 02:30 UTC on the 11th is 21:30 on the 10th in Bogotá (UTC-5): today is the 10th, which began at 05:00 UTC.
        var now = new DateTime(2026, 3, 11, 2, 30, 0, DateTimeKind.Utc);
        var queries = new Mock<IMessageFeedQueries>();
        DateTime? start = null;
        DateTime? seenNow = null;
        queries.Setup(q => q.SummaryAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Callback<DateTime, DateTime, CancellationToken>((s, n, _) => (start, seenNow) = (s, n))
            .ReturnsAsync(new MessagesSummaryData(4, null, null, 0, 0, []));
        var service = new MessageFeedService(queries.Object, new TenantTimeZone("America/Bogota"), new FixedTimeProvider(now));

        var summary = await service.SummaryAsync();

        Assert.Equal(new DateTime(2026, 3, 10, 5, 0, 0, DateTimeKind.Utc), start);
        Assert.Equal(now, seenNow);
        Assert.Equal(("2026-03-10", "America/Bogota", 4), (summary.Today, summary.TimeZone, summary.MessagesToday));
    }

    [Fact]
    public async Task TheFeedParsesTheDirectionAndTheInstant()
    {
        var queries = new Mock<IMessageFeedQueries>();
        RecentMessagesFilter? seen = null;
        queries.Setup(q => q.RecentAsync(It.IsAny<RecentMessagesFilter>(), It.IsAny<CancellationToken>()))
            .Callback<RecentMessagesFilter, CancellationToken>((f, _) => seen = f)
            .ReturnsAsync(([], 0));
        var service = new MessageFeedService(queries.Object, new TenantTimeZone("America/Bogota"), new FixedTimeProvider(DateTime.UtcNow));
        var afterId = Guid.NewGuid();

        await service.RecentAsync(new RecentMessagesQuery { Direction = "outbound", Since = new DateTimeOffset(2026, 3, 10, 10, 0, 0, TimeSpan.FromHours(-5)), AfterId = afterId, PageSize = 7 });

        Assert.Equal((MessageDirection.Outbound, new DateTime(2026, 3, 10, 15, 0, 0, DateTimeKind.Utc), afterId, 7), (seen!.Direction, seen.SinceUtc, seen.AfterId, seen.PageSize));
    }
}
