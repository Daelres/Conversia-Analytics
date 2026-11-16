using Conversia.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Conversia.IntegrationTests;

/// <summary>
/// The upgrade to the closing point (migration AddClosingPointAndAnalysisCapture) on a database that already holds conversations: the
/// stored inbound messages become the customer's, the rules are asked to read the history again, and a conversation waiting for the LLM
/// (possibly inside a paid batch) is left exactly as it was, so its answer is not discarded as stale.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class ClosingPointMigrationTests(CustomWebApplicationFactory factory)
{
    private const string PreviousMigration = "20261113195032_AddNightlyAiAnalysis";
    private static readonly DateTime Requested = new(2026, 11, 12, 6, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task TheBackfill_MarksInboundMessages_RequeuesTheRules_AndLeavesLlmPendingConversationsAlone()
    {
        await using var database = TestDatabase.Create(factory);
        await database.CreateEmptyAsync();
        await using (var old = database.NewContext())
        {
            await old.GetService<IMigrator>().MigrateAsync(PreviousMigration);
        }

        var tenant = Guid.NewGuid();
        var branch = Guid.NewGuid();
        var channel = Guid.NewGuid();
        var plain = Guid.NewGuid();
        var pending = Guid.NewGuid();
        var businessOnly = Guid.NewGuid();
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await ExecuteAsync(connection, $"""INSERT INTO "Tenants" ("Id", "Name", "Status", "CreatedAt") VALUES ('{tenant}', 'Old', 'Active', now())""");
            await ExecuteAsync(connection, $"""INSERT INTO "Branches" ("Id", "TenantId", "Name", "IsActive", "CreatedAt") VALUES ('{branch}', '{tenant}', 'Sede', true, now())""");
            await ExecuteAsync(connection, $"""
                INSERT INTO "WhatsAppChannels" ("Id", "TenantId", "BranchId", "DisplayPhoneNumber", "ChannelKey", "WebhookSecretHash", "IsActive", "CreatedAt")
                VALUES ('{channel}', '{tenant}', '{branch}', '573001234567', '{Guid.NewGuid():N}{Guid.NewGuid():N}', '\x00', true, now())
                """);
            foreach (var (conversation, inbound, llmPending, n) in new[] { (plain, true, false, 1), (pending, true, true, 2), (businessOnly, false, false, 3) })
            {
                var contact = Guid.NewGuid();
                await ExecuteAsync(connection, $"""INSERT INTO "Contacts" ("Id", "TenantId", "WaId", "FirstSeenAt", "CreatedAt") VALUES ('{contact}', '{tenant}', '57300555000{n}', now(), now())""");
                await ExecuteAsync(connection, $"""
                    INSERT INTO "Conversations" ("Id", "TenantId", "ContactId", "ChannelId", "BranchId", "Source", "StartedAt", "LastMessageAt", "FirstInboundAt",
                        "Status", "ClassificationDirty", "ClassificationRequestedAt", "ClassificationFailures", "LlmPending", "CreatedAt")
                    VALUES ('{conversation}', '{tenant}', '{contact}', '{channel}', '{branch}', 'Organic', '{Requested:O}', '{Requested:O}',
                        {(inbound ? $"'{Requested:O}'" : "NULL")}, 'Open', false, '{Requested:O}', 3, {(llmPending ? "true" : "false")}, now())
                    """);
                await ExecuteAsync(connection, $"""
                    INSERT INTO "Messages" ("Id", "TenantId", "ConversationId", "ChannelId", "ProviderMessageId", "Direction", "Type", "Text", "SentAt", "CreatedAt")
                    VALUES ('{Guid.NewGuid()}', '{tenant}', '{conversation}', '{channel}', 'wamid.in-{n}', '{(inbound ? "Inbound" : "Outbound")}', 'text', 'hola', '{Requested:O}', now())
                    """);
            }
        }

        await using (var upgraded = database.NewContext())
        {
            await upgraded.Database.MigrateAsync();
        }

        await using var db = database.NewContext();
        var conversations = await db.Conversations.IgnoreQueryFilters().AsNoTracking().ToDictionaryAsync(c => c.Id);
        Assert.True(conversations[plain].ClassificationDirty);
        Assert.Equal(0, conversations[plain].ClassificationFailures);
        Assert.NotEqual(Requested, conversations[plain].ClassificationRequestedAt);

        // Waiting for the LLM (maybe in a submitted batch): untouched, so phase 3 still finds the request time it saw.
        Assert.Equal((false, (DateTime?)Requested, 3, true),
            (conversations[pending].ClassificationDirty, conversations[pending].ClassificationRequestedAt, conversations[pending].ClassificationFailures, conversations[pending].LlmPending));
        Assert.False(conversations[businessOnly].ClassificationDirty); // the customer never wrote: nothing to read

        var messages = await db.Messages.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        Assert.All(messages.Where(m => m.Direction == MessageDirection.Inbound), m => Assert.Equal(MessageSender.Customer, m.Sender));
        Assert.All(messages.Where(m => m.Direction == MessageDirection.Outbound), m => Assert.Equal(MessageSender.Unknown, m.Sender));
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
