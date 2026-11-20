using System.Net;
using System.Net.Http.Json;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Infrastructure.Configuration;
using Conversia.Infrastructure.Conversations;
using Conversia.Infrastructure.Persistence;
using Conversia.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>
/// One rule decides who receives (<see cref="Tenant.AcceptsWebhooksIn"/>): for every status (Suspended split by cause), the webhook lookup,
/// the webhook worker and the auto-closer must all agree with it. The lines stay on here on purpose, so only the status decides.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class TenantReceivingStatesTests
{
    // An era nobody else uses (the auto-closer claims across every company).
    private static readonly DateTime Start = new(1979, 5, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public TenantReceivingStatesTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public static TheoryData<TenantStatus, SuspensionCause?> States() => new()
    {
        { TenantStatus.Pending, null },
        { TenantStatus.Active, null },
        { TenantStatus.Paused, null },
        { TenantStatus.Suspended, SuspensionCause.Manual },
        { TenantStatus.Suspended, SuspensionCause.Billing },
        { TenantStatus.Cancelled, null },
    };

    private async Task SetStateAsync(Guid tenantId, TenantStatus status, SuspensionCause? cause)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var name = status.ToString();
        var causeName = cause?.ToString();
        var cancelled = status == TenantStatus.Cancelled;
        await db.Database.ExecuteSqlAsync($"""
            UPDATE "Tenants" SET "Status" = {name}, "SuspensionCause" = {causeName},
                "CancelledAt" = CASE WHEN {cancelled} THEN now() END, "DataDeletionDueAt" = CASE WHEN {cancelled} THEN now() + interval '30 days' END
            WHERE "Id" = {tenantId}
            """);
    }

    [Theory]
    [MemberData(nameof(States))]
    public async Task TheLookupTheWorkerAndTheAutoCloser_AgreeWithTheDomainRule(TenantStatus status, SuspensionCause? cause)
    {
        var receives = Tenant.AcceptsWebhooksIn(status, cause);
        using var f = await StartChannelAsync(_factory, _client);
        await ClearQueueAsync(_factory.Services);
        await DeliverAsync(_client, f, Dialog360Samples.InboundText(id: $"wamid.STATE-{Guid.NewGuid():N}", waId: UniqueWaId())); // queued while Active
        var idle = await SeedConversationAsync(_factory.Services, f, Start, [In(0, "Hola")]);

        await SetStateAsync(f.TenantId, status, cause);

        using (var webhook = await PostWebhookAsync(_client, f.Path, f.Secret, Dialog360Samples.InboundText(id: $"wamid.STATE2-{Guid.NewGuid():N}", waId: UniqueWaId())))
        {
            Assert.Equal(receives ? HttpStatusCode.OK : HttpStatusCode.Unauthorized, webhook.StatusCode);
        }

        await DrainAsync(RunnerOf(_factory.Services));
        var events = (await SnapshotAsync(_factory.Services, f.TenantId)).Events;
        Assert.Equal(receives, events.All(e => e.Status == WebhookEventStatus.Processed));

        using (var scope = _factory.Services.CreateScope())
        {
            var closer = new ConversationAutoCloser(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(), new ConversationLifecycleOptions(true, 7, 15, 200));
            while ((await closer.CloseBatchAsync(Start.AddDays(30))).Closed > 0)
            {
            }
        }

        Assert.Equal(receives ? ConversationStatus.Closed : ConversationStatus.Open, (await LoadAsync(_factory.Services, idle.ConversationId)).Conversation.Status);
    }

    [Fact]
    public async Task AnInvalidTransition_IsA409_AndChangesNothing()
    {
        using var admin = await StartAdminAsync(_factory, _client);
        using var tenant = await StartTenantAsync(_factory, _client, "invalidmove");
        Assert.Equal(HttpStatusCode.OK, (await admin.Client.PutAsJsonAsync($"{AdminTenantsUrl}/{tenant.TenantId}/status", new { status = "Paused" })).StatusCode);

        var again = await admin.Client.PutAsJsonAsync($"{AdminTenantsUrl}/{tenant.TenantId}/status", new { status = "Paused" });
        var back = await admin.Client.PutAsJsonAsync($"{AdminTenantsUrl}/{tenant.TenantId}/status", new { status = "Pending" });

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, back.StatusCode);
        Assert.Equal(TenantStatus.Paused, (await GetAdminTenantAsync(admin, tenant.TenantId)).Status);
    }

    [Fact]
    public async Task AnAdministrator_TurnsABillingSuspensionIntoAManualOne_AndTheWebhooksStop()
    {
        using var admin = await StartAdminAsync(_factory, _client);
        using var f = await StartChannelAsync(_factory, _client);
        using (var scope = _factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<Conversia.Application.Interfaces.ITenantBillingLifecycle>()
                .SuspendForBillingAsync(f.TenantId, Conversia.Domain.Common.ReasonCodes.BillingPastDue);
        }

        var moved = await admin.Client.PutAsJsonAsync($"{AdminTenantsUrl}/{f.TenantId}/status",
            new { status = "Suspended", reason = "Disputa del cobro", reasonCode = Conversia.Domain.Common.AdminReasonCodes.PaymentIssue });

        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        Assert.Equal(SuspensionCause.Manual, (await GetAdminTenantAsync(admin, f.TenantId)).SuspensionCause);
        using var refused = await PostWebhookAsync(_client, f.Path, f.Secret, Dialog360Samples.InboundText(id: $"wamid.CONV-{Guid.NewGuid():N}"));
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        var unknown = await admin.Client.PutAsJsonAsync($"{AdminTenantsUrl}/{f.TenantId}/status", new { status = "Active", reasonCode = "admin.porque_si" });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    }
}
