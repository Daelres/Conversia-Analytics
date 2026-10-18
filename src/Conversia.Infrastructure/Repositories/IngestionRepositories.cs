using Conversia.Application.Exceptions;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Domain.Interfaces;
using Conversia.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Conversia.Infrastructure.Repositories;

// Every query is scoped to the current tenant by the global query filter of each entity; none repeats a TenantId predicate.

public class WebhookEventRepository(ApplicationDbContext dbContext, TimeProvider timeProvider) : IWebhookEventRepository
{
    public async Task<bool> AddIfNewAsync(WebhookEvent webhookEvent, CancellationToken cancellationToken = default)
    {
        // INSERT ... ON CONFLICT DO NOTHING: the provider re-sending the same body is routine, so it must not be an exception (and with it an
        // error log line that can quote the payload). The unique (ChannelId, BodyHash) index is still the arbiter of concurrent deliveries.
        // The audit date is stamped here because this path does not go through SaveChanges.
        var createdAt = DatabaseTime.Truncate(timeProvider.GetUtcNow().UtcDateTime);
        var status = webhookEvent.Status.ToString();

        try
        {
            var inserted = await dbContext.Database.ExecuteSqlAsync($"""
                INSERT INTO "WebhookEvents"
                    ("Id", "TenantId", "ChannelId", "BodyHash", "RawPayload", "ReceivedAt", "Status", "Attempts", "NextAttemptAt", "CreatedAt")
                VALUES
                    ({webhookEvent.Id}, {webhookEvent.TenantId}, {webhookEvent.ChannelId}, {webhookEvent.BodyHash}, {webhookEvent.RawPayload}::jsonb,
                     {webhookEvent.ReceivedAt}, {status}, {webhookEvent.Attempts}, {webhookEvent.NextAttemptAt}, {createdAt})
                ON CONFLICT ("ChannelId", "BodyHash") DO NOTHING
                """, cancellationToken);
            return inserted > 0;
        }
        catch (PostgresException ex) when (ex.SqlState.StartsWith("22", StringComparison.Ordinal))
        {
            // SQLSTATE class 22 (data exception): JSON that jsonb cannot hold. The service already refuses what it can recognize, so this is the backstop.
            throw new InvalidWebhookPayloadException();
        }
    }
}

public class ContactRepository(ApplicationDbContext dbContext) : IContactRepository
{
    public Task<Contact?> GetByWaIdAsync(string waId, CancellationToken cancellationToken = default)
        => dbContext.Contacts.FirstOrDefaultAsync(c => c.WaId == waId, cancellationToken);

    public async Task AddAsync(Contact contact, CancellationToken cancellationToken = default)
    {
        await dbContext.Contacts.AddAsync(contact, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public Task UpdateAsync(Contact contact, CancellationToken cancellationToken = default)
        => dbContext.SaveChangesAsync(cancellationToken);
}

public class ConversationRepository(ApplicationDbContext dbContext) : IConversationRepository
{
    public Task<Conversation?> FindOpenAsync(Guid contactId, Guid channelId, CancellationToken cancellationToken = default)
        => dbContext.Conversations.FirstOrDefaultAsync(
            c => c.ContactId == contactId && c.ChannelId == channelId && c.Status == ConversationStatus.Open, cancellationToken);

    public async Task AddAsync(Conversation conversation, Opportunity opportunity, CancellationToken cancellationToken = default)
    {
        await dbContext.Conversations.AddAsync(conversation, cancellationToken);
        await dbContext.Opportunities.AddAsync(opportunity, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public Task UpdateAsync(Conversation conversation, CancellationToken cancellationToken = default)
        => dbContext.SaveChangesAsync(cancellationToken);
}

public class MessageRepository(ApplicationDbContext dbContext) : IMessageRepository
{
    public Task<bool> ExistsAsync(Guid channelId, string providerMessageId, CancellationToken cancellationToken = default)
        => dbContext.Messages.AnyAsync(m => m.ChannelId == channelId && m.ProviderMessageId == providerMessageId, cancellationToken);

    public async Task AddAsync(Message message, CancellationToken cancellationToken = default)
    {
        await dbContext.Messages.AddAsync(message, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<(MessageDirection Direction, DateTime SentAt)>> ListTimesAsync(
        Guid conversationId, CancellationToken cancellationToken = default)
    {
        var rows = await dbContext.Messages.AsNoTracking()
            .Where(m => m.ConversationId == conversationId)
            .Select(m => new { m.Direction, m.SentAt })
            .ToListAsync(cancellationToken);

        return rows.Select(r => (r.Direction, r.SentAt)).ToList();
    }
}
