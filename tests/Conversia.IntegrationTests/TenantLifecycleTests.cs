using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Conversia.Application.DTOs;
using Conversia.Application.DTOs.Admin;
using Conversia.Application.DTOs.Tenants;
using Conversia.Application.Interfaces;
using Conversia.Domain.Common;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Infrastructure.Classification;
using Conversia.Infrastructure.Configuration;
using Conversia.Infrastructure.Persistence;
using Conversia.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>
/// The lifecycle of a company end to end (docs/PLATFORM_ADMIN.md, "Lifecycle of a company"): a pause and a billing suspension keep every
/// message and stop the users and the analysis; a manual suspension and a cancellation refuse the webhooks; a cancellation switches the
/// lines off, releases the licences and schedules the deletion; the billing hooks are idempotent and audited as the system.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class TenantLifecycleTests
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public TenantLifecycleTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private static async Task<HttpResponseMessage> SetStatusAsync(TestAdmin admin, Guid tenantId, string status, string? reason = null)
        => await admin.Client.PutAsJsonAsync($"{AdminTenantsUrl}/{tenantId}/status", new { status, reason });

    private static async Task AssertNotActiveAsync(HttpResponseMessage response, string status, string? cause, string detail)
    {
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("tenant_not_active", body.RootElement.GetProperty("code").GetString());
        Assert.Equal(status, body.RootElement.GetProperty("tenantStatus").GetString());
        Assert.Equal(detail, body.RootElement.GetProperty("detail").GetString());
        if (cause is null)
        {
            Assert.False(body.RootElement.TryGetProperty("suspensionCause", out _));
        }
        else
        {
            Assert.Equal(cause, body.RootElement.GetProperty("suspensionCause").GetString());
        }
    }

    private async Task<TenantInfoResponse> TenantInfoAsync(TestActor actor)
    {
        var response = await actor.Client.GetAsync("/api/tenant");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadBodyAsync<TenantInfoResponse>(response);
    }

    private async Task ClassifyAllAsync(DateTime now)
    {
        var runner = new ConversationClassificationRunner(
            _factory.Services.GetRequiredService<IServiceScopeFactory>(), ClassificationOptions.Default with { DebounceSeconds = 0 }, new FixedTimeProvider(now),
            NullLogger<ConversationClassificationRunner>.Instance);
        while (await runner.ProcessNextAsync())
        {
        }
    }

    private async Task<Conversation> TheConversationOfAsync(Guid tenantId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Conversations.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.TenantId == tenantId);
    }

    private async Task<IReadOnlyList<AuditEntryResponse>> LifecycleAsync(TestAdmin admin, Guid tenantId)
    {
        var response = await admin.Client.GetAsync($"{AdminTenantsUrl}/{tenantId}/lifecycle");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ReadBodyAsync<PagedResponse<AuditEntryResponse>>(response)).Items;
    }

    [Fact]
    public async Task APausedCompany_KeepsReceivingAndStoring_ButItsUsersAndItsAnalysisWait_UntilItResumes()
    {
        using var admin = await StartAdminAsync(_factory, _client);
        using var f = await StartChannelAsync(_factory, _client);
        await ClearQueueAsync(_factory.Services);

        Assert.Equal(HttpStatusCode.OK, (await SetStatusAsync(admin, f.TenantId, "Paused", "Vacaciones colectivas")).StatusCode);

        await AssertNotActiveAsync(await f.Tenant.Owner.Client.GetAsync("/api/conversations"), "Paused", null, "Tu empresa está en pausa.");
        Assert.Equal(TenantStatus.Paused, (await TenantInfoAsync(f.Tenant.Owner)).Status);

        // The provider keeps delivering and nothing is lost: the delivery is stored and turned into a message.
        await DeliverAsync(_client, f, Dialog360Samples.InboundText(id: $"wamid.PAUSE-{Guid.NewGuid():N}", waId: UniqueWaId()));
        await DrainAsync(RunnerOf(_factory.Services));
        var stored = await SnapshotAsync(_factory.Services, f.TenantId);
        Assert.Single(stored.Messages);

        // The analysis waits for the company to be Active again.
        await ClassifyAllAsync(DateTime.UtcNow.AddDays(1));
        Assert.True((await TheConversationOfAsync(f.TenantId)).ClassificationDirty);

        Assert.Equal(HttpStatusCode.OK, (await SetStatusAsync(admin, f.TenantId, "Active", "Fin de la pausa")).StatusCode);
        await ClassifyAllAsync(DateTime.UtcNow.AddDays(1));
        Assert.False((await TheConversationOfAsync(f.TenantId)).ClassificationDirty);
        Assert.Equal(HttpStatusCode.OK, (await f.Tenant.Owner.Client.GetAsync("/api/conversations")).StatusCode);
    }

    [Fact]
    public async Task ABillingSuspension_KeepsTheMessages_TellsTheUsersAPaymentIsPending_AndIsAuditedAsTheSystem()
    {
        using var admin = await StartAdminAsync(_factory, _client);
        using var f = await StartChannelAsync(_factory, _client);
        await ClearQueueAsync(_factory.Services);

        using (var scope = _factory.Services.CreateScope())
        {
            var billing = scope.ServiceProvider.GetRequiredService<ITenantBillingLifecycle>();
            Assert.Equal(TenantLifecycleOutcome.Changed, await billing.SuspendForBillingAsync(f.TenantId, ReasonCodes.BillingPastDue));
            Assert.Equal(TenantLifecycleOutcome.Unchanged, await billing.SuspendForBillingAsync(f.TenantId, ReasonCodes.BillingPastDue));
        }

        await AssertNotActiveAsync(
            await f.Tenant.Owner.Client.GetAsync("/api/conversations"), "Suspended", "Billing", "Tu empresa está suspendida por un pago pendiente.");
        Assert.Equal(SuspensionCause.Billing, (await TenantInfoAsync(f.Tenant.Owner)).SuspensionCause);

        await DeliverAsync(_client, f, Dialog360Samples.InboundText(id: $"wamid.BILL-{Guid.NewGuid():N}", waId: UniqueWaId()));
        await DrainAsync(RunnerOf(_factory.Services));
        Assert.Single((await SnapshotAsync(_factory.Services, f.TenantId)).Messages);

        var entry = Assert.Single(await LifecycleAsync(admin, f.TenantId));
        Assert.Null(entry.AdminId);
        Assert.Equal(ReasonCodes.BillingPastDue, entry.ReasonCode);
        Assert.Equal("Billing", entry.Details.GetProperty("cause").GetString());

        using (var scope = _factory.Services.CreateScope())
        {
            var billing = scope.ServiceProvider.GetRequiredService<ITenantBillingLifecycle>();
            Assert.Equal(TenantLifecycleOutcome.Changed, await billing.ResumeFromBillingAsync(f.TenantId, ReasonCodes.BillingPaid));
        }

        Assert.Equal(TenantStatus.Active, (await TenantInfoAsync(f.Tenant.Owner)).Status);
        Assert.Equal(2, (await LifecycleAsync(admin, f.TenantId)).Count);
    }

    [Fact]
    public async Task AManualSuspension_StillRefusesTheWebhooks_AndAPaymentDoesNotLiftIt()
    {
        using var admin = await StartAdminAsync(_factory, _client);
        using var f = await StartChannelAsync(_factory, _client);

        Assert.Equal(HttpStatusCode.OK, (await SetStatusAsync(admin, f.TenantId, "Suspended", "Revisión")).StatusCode);

        using var refused = await PostWebhookAsync(_client, f.Path, f.Secret, Dialog360Samples.InboundText(id: $"wamid.SUSP-{Guid.NewGuid():N}"));
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Empty((await SnapshotAsync(_factory.Services, f.TenantId)).Events);
        await AssertNotActiveAsync(await f.Tenant.Owner.Client.GetAsync("/api/conversations"), "Suspended", "Manual", "Tu empresa está suspendida.");

        using var scope = _factory.Services.CreateScope();
        Assert.Equal(TenantLifecycleOutcome.NotApplicable,
            await scope.ServiceProvider.GetRequiredService<ITenantBillingLifecycle>().ResumeFromBillingAsync(f.TenantId, ReasonCodes.BillingPaid));
    }

    [Fact]
    public async Task CancellingACompany_SwitchesItsLinesOff_ReleasesItsLicences_RefusesItsWebhooks_AndSchedulesTheDeletion_UntilItIsReactivated()
    {
        using var admin = await StartAdminAsync(_factory, _client);
        using var f = await StartChannelAsync(_factory, _client, phone: $"+57 301 {Random.Shared.Next(100, 999)} {Random.Shared.Next(1000, 9999)}");
        var license = await admin.Client.PostAsJsonAsync("/api/admin/licenses", new { phoneNumber = f.Channel.DisplayPhoneNumber, monthlyCostUsd = 10m, customerOwned = false });
        Assert.Equal(HttpStatusCode.Created, license.StatusCode);
        var licenseId = (await ReadBodyAsync<LineLicenseResponse>(license)).Id;
        Assert.Equal(HttpStatusCode.OK, (await admin.Client.PostAsJsonAsync($"/api/admin/licenses/{licenseId}/assign", new { tenantId = f.TenantId })).StatusCode);

        var cancelled = await SetStatusAsync(admin, f.TenantId, "Cancelled", "Fin del contrato");

        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        var detail = await GetAdminTenantAsync(admin, f.TenantId) as TenantDetailResponse;
        Assert.Equal(TenantStatus.Cancelled, detail!.Status);
        Assert.All(detail.Lines, l => Assert.False(l.IsActive));
        Assert.Empty(detail.Licenses);
        Assert.InRange(detail.DataDeletionDueAt!.Value, DateTime.UtcNow.AddDays(29.9), DateTime.UtcNow.AddDays(30.1));

        using var refused = await PostWebhookAsync(_client, f.Path, f.Secret, Dialog360Samples.InboundText(id: $"wamid.CANC-{Guid.NewGuid():N}"));
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        await AssertNotActiveAsync(await f.Tenant.Owner.Client.GetAsync("/api/conversations"), "Cancelled", null, "El servicio de tu empresa fue cancelado.");
        Assert.NotNull((await TenantInfoAsync(f.Tenant.Owner)).DataDeletionDueAt);

        // Nothing new can be given to a cancelled company.
        var again = await admin.Client.PostAsJsonAsync($"/api/admin/licenses/{licenseId}/assign", new { tenantId = f.TenantId });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Contains("tenant_cancelled", await again.Content.ReadAsStringAsync());

        var history = await LifecycleAsync(admin, f.TenantId);
        var entry = Assert.Single(history);
        Assert.Equal(admin.AdminId, entry.AdminId);
        Assert.Equal(1, entry.Details.GetProperty("linesDeactivated").GetInt32());
        Assert.Equal(1, entry.Details.GetProperty("licensesReleased").GetInt32());

        // Reactivated before the deletion: Active again, the schedule gone, the lines off until the company switches them on.
        Assert.Equal(HttpStatusCode.OK, (await SetStatusAsync(admin, f.TenantId, "Active", "Renovó")).StatusCode);
        var back = (TenantDetailResponse)await GetAdminTenantAsync(admin, f.TenantId);
        Assert.Equal(TenantStatus.Active, back.Status);
        Assert.Null(back.DataDeletionDueAt);
        Assert.All(back.Lines, l => Assert.False(l.IsActive));
    }

    [Fact]
    public async Task DeleteNow_IsRefusedUnlessCancelled_NeedsTheExactName_AndAReason_AndThenBlocksTheReactivation()
    {
        using var admin = await StartAdminAsync(_factory, _client);
        using var tenant = await StartTenantAsync(_factory, _client, "deletenow");
        var name = (await GetAdminTenantAsync(admin, tenant.TenantId)).Name;
        var url = $"{AdminTenantsUrl}/{tenant.TenantId}/data-deletion";

        var notCancelled = await admin.Client.PostAsJsonAsync(url, new { confirmName = name, reason = "Solicitud del cliente" });
        Assert.Equal(HttpStatusCode.Conflict, notCancelled.StatusCode);
        Assert.Contains("tenant_not_cancelled", await notCancelled.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, (await SetStatusAsync(admin, tenant.TenantId, "Cancelled")).StatusCode);

        var wrongName = await admin.Client.PostAsJsonAsync(url, new { confirmName = name + " x", reason = "Solicitud del cliente" });
        Assert.Equal(HttpStatusCode.BadRequest, wrongName.StatusCode);
        await AssertValidationErrorUnderAsync(await admin.Client.PostAsJsonAsync(url, new { confirmName = name }), "reason");
        Assert.Null((await GetAdminTenantAsync(admin, tenant.TenantId)).DataDeletionStartedAt);

        var accepted = await admin.Client.PostAsJsonAsync(url, new { confirmName = name.ToUpperInvariant(), reason = "Solicitud del cliente" });
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.NotNull((await ReadBodyAsync<TenantSummaryResponse>(accepted)).DataDeletionStartedAt);
        Assert.Equal(HttpStatusCode.Accepted, (await admin.Client.PostAsJsonAsync(url, new { confirmName = name, reason = "Otra vez" })).StatusCode);

        var reactivate = await SetStatusAsync(admin, tenant.TenantId, "Active");
        Assert.Equal(HttpStatusCode.Conflict, reactivate.StatusCode);
        Assert.Contains("tenant_deletion_started", await reactivate.Content.ReadAsStringAsync());

        var history = await LifecycleAsync(admin, tenant.TenantId);
        var started = Assert.Single(history, e => e.Action == PlatformAuditLog.Actions.TenantDataDeletionStarted);
        Assert.Equal(ReasonCodes.DeletionRequestedByAdmin, started.ReasonCode);
        Assert.Equal("Manual", started.Details.GetProperty("trigger").GetString());

        using var scope = _factory.Services.CreateScope();
        var tombstone = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().TenantDeletions.AsNoTracking().SingleAsync(d => d.TenantId == tenant.TenantId);
        Assert.Equal(_factory.Services.GetRequiredService<Conversia.Application.Privacy.PrivacyHasher>().TenantName(name).Hex, tombstone.NameHash);
        Assert.Equal(admin.AdminId, tombstone.RequestedBy);
    }

    [Fact]
    public async Task TheAdminListing_FiltersByTheNewStatuses()
    {
        using var admin = await StartAdminAsync(_factory, _client);
        using var paused = await StartTenantAsync(_factory, _client, "pausedlist");
        Assert.Equal(HttpStatusCode.OK, (await SetStatusAsync(admin, paused.TenantId, "Paused")).StatusCode);

        var page = await ReadBodyAsync<PagedResponse<TenantSummaryResponse>>(await admin.Client.GetAsync($"{AdminTenantsUrl}?status=Paused&pageSize=100"));

        Assert.Contains(page.Items, t => t.Id == paused.TenantId);
        Assert.All(page.Items, t => Assert.Equal(TenantStatus.Paused, t.Status));
    }
}
