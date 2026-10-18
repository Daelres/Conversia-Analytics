using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>The constraints and conventions of the conversation tables that ingestion and the metrics rely on, checked on a real PostgreSQL.</summary>
[Collection(nameof(IntegrationTestCollection))]
public class ConversationModelSchemaTests
{
    private static readonly DateTime T0 = new(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public ConversationModelSchemaTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private sealed record Graph(Guid TenantId, Branch Branch, WhatsAppChannel Channel, Contact Contact);

    private async Task<Graph> SeedGraphAsync()
    {
        var session = await RegisterTenantSessionAsync(_client);
        var branch = Branch.Create(session.TenantId, UniqueName("Sede"));
        var (channel, _) = WhatsAppChannel.Create(session.TenantId, branch.Id, "3001234567");
        var contact = Contact.Create(session.TenantId, "573001234567", "Laura", T0);
        await using var scope = Scope(out var db);
        db.AddRange(branch, channel, contact);
        await db.SaveChangesAsync();
        return new Graph(session.TenantId, branch, channel, contact);
    }

    private AsyncServiceScope Scope(out ApplicationDbContext db)
    {
        var scope = _factory.Services.CreateAsyncScope();
        db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return scope;
    }

    private static Conversation NewConversation(Graph g)
        => Conversation.Start(g.TenantId, g.Contact.Id, g.Channel.Id, g.Branch.Id, T0, ConversationSource.Organic);

    private static async Task<DbUpdateException> UniqueViolationAsync(ApplicationDbContext db)
    {
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.True(ex.IsUniqueViolation(), ex.InnerException?.Message);
        return ex;
    }

    // ErrorLog is the operations table: it names a tenant as data and is deliberately unfiltered.
    [Fact]
    public void EveryTenantScopedEntityHasTheTenantQueryFilter()
    {
        using var scope = Scope(out var db);

        var unfiltered = db.Model.GetEntityTypes()
            .Where(t => t.FindProperty("TenantId") is not null && t.ClrType != typeof(ErrorLog) && t.GetDeclaredQueryFilters().Count == 0)
            .Select(t => t.ClrType.Name);

        Assert.Empty(unfiltered);
    }

    [Fact]
    public async Task OnlyOneOpenConversationPerContactAndChannel_ButAClosedOneDoesNotCount()
    {
        var g = await SeedGraphAsync();
        await using (Scope(out var db))
        {
            db.Add(NewConversation(g));
            await db.SaveChangesAsync();
        }

        await using (Scope(out var db))
        {
            db.Add(NewConversation(g));
            await UniqueViolationAsync(db);
        }

        await using (Scope(out var db))
        {
            await db.Database.ExecuteSqlAsync($"UPDATE \"Conversations\" SET \"Status\" = 'Closed' WHERE \"ContactId\" = {g.Contact.Id}");
            db.Add(NewConversation(g));
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task AProviderMessageIdIsUniquePerChannel()
    {
        var g = await SeedGraphAsync();
        var conversation = NewConversation(g);
        await using (Scope(out var db))
        {
            db.Add(conversation);
            db.Add(Message.Create(g.TenantId, conversation.Id, g.Channel.Id, "wamid.1", MessageDirection.Inbound, "text", "hola", T0));
            await db.SaveChangesAsync();
        }

        await using (Scope(out var db))
        {
            db.Add(Message.Create(g.TenantId, conversation.Id, g.Channel.Id, "wamid.1", MessageDirection.Outbound, "text", "otra vez", T0));
            await UniqueViolationAsync(db);
        }

        await using (Scope(out var db))
        {
            db.Add(Message.Create(g.TenantId, conversation.Id, g.Channel.Id, "WAMID.1", MessageDirection.Outbound, "text", "ids are case-sensitive", T0));
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task AContactIsUniquePerTenantAndWaId_AndAnOpportunityPerConversation()
    {
        var g = await SeedGraphAsync();
        var conversation = NewConversation(g);
        await using (Scope(out var db))
        {
            db.AddRange(conversation, Opportunity.Open(g.TenantId, conversation.Id));
            await db.SaveChangesAsync();
        }

        await using (Scope(out var db))
        {
            db.Add(Contact.Create(g.TenantId, "+573001234567", null, T0));
            await UniqueViolationAsync(db);
        }

        await using (Scope(out var db))
        {
            db.Add(Opportunity.Open(g.TenantId, conversation.Id));
            await UniqueViolationAsync(db);
        }
    }

    [Fact]
    public async Task AWebhookBodyHashIsUniquePerChannel()
    {
        var g = await SeedGraphAsync();
        var hash = new byte[32];
        hash[0] = 7;
        await using (Scope(out var db))
        {
            db.Add(WebhookEvent.Receive(g.TenantId, g.Channel.Id, hash, "{\"a\":1}", T0));
            await db.SaveChangesAsync();
        }

        await using (Scope(out var db))
        {
            db.Add(WebhookEvent.Receive(g.TenantId, g.Channel.Id, hash, "{\"a\":1}", T0));
            await UniqueViolationAsync(db);
        }
    }

    [Fact]
    public async Task EnumsAreStoredByName_AndTheRawPayloadAsJsonb()
    {
        var g = await SeedGraphAsync();
        var conversation = Conversation.Start(g.TenantId, g.Contact.Id, g.Channel.Id, g.Branch.Id, T0, ConversationSource.Ads, "headline");
        var webhookEvent = WebhookEvent.Receive(g.TenantId, g.Channel.Id, new byte[32], "{ \"b\": 2,   \"a\": 1 }", T0);
        await using var scope = Scope(out var db);
        db.AddRange(conversation, Opportunity.Open(g.TenantId, conversation.Id), webhookEvent);
        await db.SaveChangesAsync();

        var source = await db.Database.SqlQuery<string>($"SELECT \"Source\" AS \"Value\" FROM \"Conversations\" WHERE \"Id\" = {conversation.Id}").SingleAsync();
        var stage = await db.Database.SqlQuery<string>($"SELECT \"Stage\" AS \"Value\" FROM \"Opportunities\" WHERE \"ConversationId\" = {conversation.Id}").SingleAsync();
        var status = await db.Database.SqlQuery<string>($"SELECT \"Status\" AS \"Value\" FROM \"WebhookEvents\" WHERE \"Id\" = {webhookEvent.Id}").SingleAsync();
        var payloadType = await db.Database.SqlQuery<string>($"SELECT pg_typeof(\"RawPayload\")::text AS \"Value\" FROM \"WebhookEvents\" WHERE \"Id\" = {webhookEvent.Id}").SingleAsync();

        Assert.Equal(("Ads", "New", "Pending", "jsonb"), (source, stage, status, payloadType));
    }

    [Fact]
    public async Task TheMetricsIndexesExist()
    {
        await using var scope = Scope(out var db);

        var indexes = await db.Database.SqlQuery<string>(
            $"SELECT indexname AS \"Value\" FROM pg_indexes WHERE tablename IN ('Conversations', 'Messages', 'WebhookEvents')").ToListAsync();

        Assert.Contains("IX_Conversations_BranchId_StartedAt", indexes);
        Assert.Contains("IX_Conversations_AdvisorId_StartedAt", indexes);
        Assert.Contains("IX_Messages_ConversationId_SentAt", indexes);
        Assert.Contains("IX_Messages_ChannelId_ProviderMessageId", indexes);
        Assert.Contains("IX_WebhookEvents_ChannelId_BodyHash", indexes);
    }
}
