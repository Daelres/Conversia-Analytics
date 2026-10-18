using Conversia.Application.Exceptions;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Domain.Interfaces;
using Conversia.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Conversia.Infrastructure.Repositories;

// Every query is scoped to the current tenant by the global query filter of each entity; none repeats a TenantId predicate.

public class WebhookEventRepository(ApplicationDbContext dbContext) : IWebhookEventRepository
{
    public async Task<bool> AddIfNewAsync(WebhookEvent webhookEvent, CancellationToken cancellationToken = default)
    {
        await dbContext.WebhookEvents.AddAsync(webhookEvent, cancellationToken);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            // The unique (ChannelId, BodyHash) index is the arbiter: the provider delivered this exact body before.
            dbContext.Entry(webhookEvent).State = EntityState.Detached;
            return false;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg && pg.SqlState.StartsWith("22", StringComparison.Ordinal))
        {
            // SQLSTATE class 22 (data exception): well-formed JSON that jsonb cannot hold, such as a \u0000 escape.
            dbContext.Entry(webhookEvent).State = EntityState.Detached;
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
