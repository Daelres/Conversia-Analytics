using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Conversia.IntegrationTests;

/// <summary>Seeding of conversations straight into the database, for tests of the read, lifecycle, classification and metrics code.</summary>
internal static partial class TestHelpers
{
    /// <summary>One message of a seeded conversation: who wrote it, how many seconds after the conversation's start, and the text.</summary>
    internal readonly record struct Msg(MessageDirection Direction, int Seconds, string? Text = null, string Type = "text");

    internal static Msg In(int seconds, string? text = "Hola") => new(MessageDirection.Inbound, seconds, text);

    internal static Msg Out(int seconds, string? text = "¡Hola! Con gusto te ayudo.") => new(MessageDirection.Outbound, seconds, text);

    internal sealed record Seeded(Guid ConversationId, Guid ContactId, Guid ChannelId, Guid BranchId, Guid TenantId);

    private static long _waIdCounter = 5_730_000_0000L;

    /// <summary>A phone number no other seeded contact has (digits only: the strict WhatsApp id rule).</summary>
    internal static string UniqueWaId() => Interlocked.Increment(ref _waIdCounter).ToString();

    /// <param name="startedAt">UTC time of the conversation's first message.</param>
    /// <param name="messages">The conversation's messages; the timeline (start, last message, first inbound, first response) is derived from them like ingestion does.</param>
    internal static async Task<Seeded> SeedConversationAsync(
        IServiceProvider services,
        Guid tenantId,
        Guid branchId,
        Guid channelId,
        DateTime startedAt,
        IReadOnlyList<Msg> messages,
        ConversationSource source = ConversationSource.Organic,
        Guid? advisorId = null,
        bool closed = false,
        string? waId = null,
        Action<Opportunity>? configureOpportunity = null)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var contact = Contact.Create(tenantId, waId ?? UniqueWaId(), "Cliente", startedAt);
        var conversation = Conversation.Start(tenantId, contact.Id, channelId, branchId, startedAt, source);
        if (advisorId is { } advisor)
        {
            conversation.AssignAdvisor(advisor);
        }

        var opportunity = Opportunity.Open(tenantId, conversation.Id);
        configureOpportunity?.Invoke(opportunity);

        var stored = messages
            .Select(m => Message.Create(tenantId, conversation.Id, channelId, $"wamid.{Guid.NewGuid():N}", m.Direction, m.Type, m.Text, startedAt.AddSeconds(m.Seconds)))
            .ToList();
        if (stored.Count > 0)
        {
            conversation.ApplyTimeline(ConversationTimeline.From(stored.Select(m => (m.Direction, m.SentAt)))!);
        }

        if (closed)
        {
            conversation.Close(conversation.LastMessageAt);
        }

        db.AddRange(contact, conversation, opportunity);
        db.AddRange(stored);
        await db.SaveChangesAsync();
        return new Seeded(conversation.Id, contact.Id, channelId, branchId, tenantId);
    }

    internal static Task<Seeded> SeedConversationAsync(
        IServiceProvider services, ChannelFixture f, DateTime startedAt, IReadOnlyList<Msg> messages,
        ConversationSource source = ConversationSource.Organic, Guid? advisorId = null, bool closed = false, string? waId = null,
        Action<Opportunity>? configureOpportunity = null)
        => SeedConversationAsync(services, f.TenantId, f.Branch.Id, f.Channel.Id, startedAt, messages, source, advisorId, closed, waId, configureOpportunity);

    /// <summary>The stored conversation and its opportunity, bypassing the tenant filter.</summary>
    internal static async Task<(Conversation Conversation, Opportunity Opportunity)> LoadAsync(IServiceProvider services, Guid conversationId)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return (
            await db.Conversations.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == conversationId),
            await db.Opportunities.IgnoreQueryFilters().AsNoTracking().SingleAsync(o => o.ConversationId == conversationId));
    }
}
