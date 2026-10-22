using Conversia.Domain.Enums;
using Conversia.Infrastructure.Classification;
using Conversia.Infrastructure.Configuration;
using Conversia.Infrastructure.Conversations;
using Conversia.Infrastructure.Persistence;
using Conversia.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net.Http.Json;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>The classifier and the auto-closer skip the conversations of a company that is not Active, and pick them up when it is Active again.</summary>
[Collection(nameof(IntegrationTestCollection))]
public class TenantWorkersTests
{
    // An era nobody else uses (the workers claim across every company).
    private static readonly DateTime Start = new(1980, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public TenantWorkersTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private Task SetStatusAsync(TestAdmin admin, Guid tenantId, string status)
        => admin.Client.PutAsJsonAsync($"{AdminTenantsUrl}/{tenantId}/status", new { status }).ContinueWith(t => Assert.True(t.Result.IsSuccessStatusCode));

    [Fact]
    public async Task TheClassifier_SkipsASuspendedCompany_AndClassifiesItAfterReactivation()
    {
        using var admin = await StartAdminAsync(_factory, _client);
        using var f = await StartChannelAsync(_factory, _client);
        var seeded = await SeedConversationAsync(_factory.Services, f, Start, [In(0, "Hola, cuánto cuesta?"), Out(60, "Son $60.000."), In(120, "No gracias")], requestClassification: true);
        var clock = new FixedTimeProvider(Start.AddDays(2));
        var runner = new ConversationClassificationRunner(_factory.Services.GetRequiredService<IServiceScopeFactory>(), ClassificationOptions.Default with { DebounceSeconds = 60 }, clock, NullLogger<ConversationClassificationRunner>.Instance);

        await SetStatusAsync(admin, f.TenantId, "Suspended");
        while (await runner.ProcessNextAsync())
        {
        }

        Assert.True((await LoadAsync(_factory.Services, seeded.ConversationId)).Conversation.ClassificationDirty);

        await SetStatusAsync(admin, f.TenantId, "Active");
        while (await runner.ProcessNextAsync())
        {
        }

        Assert.False((await LoadAsync(_factory.Services, seeded.ConversationId)).Conversation.ClassificationDirty);
    }

    [Fact]
    public async Task TheAutoCloser_SkipsASuspendedCompany_AndClosesItsConversationsAfterReactivation()
    {
        using var admin = await StartAdminAsync(_factory, _client);
        using var f = await StartChannelAsync(_factory, _client);
        var seeded = await SeedConversationAsync(_factory.Services, f, Start, [In(0, "Hola")]);
        var now = Start.AddDays(20);

        await SetStatusAsync(admin, f.TenantId, "Suspended");
        await CloseAsync(now);
        Assert.Equal(ConversationStatus.Open, (await LoadAsync(_factory.Services, seeded.ConversationId)).Conversation.Status);

        await SetStatusAsync(admin, f.TenantId, "Active");
        await CloseAsync(now);
        Assert.Equal(ConversationStatus.Closed, (await LoadAsync(_factory.Services, seeded.ConversationId)).Conversation.Status);
    }

    private async Task CloseAsync(DateTime now)
    {
        using var scope = _factory.Services.CreateScope();
        var closer = new ConversationAutoCloser(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(), new ConversationLifecycleOptions(true, 7, 15, 200));
        while ((await closer.CloseBatchAsync(now)).Closed > 0)
        {
        }
    }
}
