using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Conversia.Application.DTOs.Organization;
using Conversia.Application.DTOs.Tenants;
using Conversia.Domain.Enums;
using Conversia.Tests.Shared;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>A company that is not Active cannot operate: its API answers 403 with a stable code, its webhooks answer 401, and it comes back when it is activated.</summary>
[Collection(nameof(IntegrationTestCollection))]
public class TenantEnforcementTests
{
    // One representative of each family of company endpoints.
    public static TheoryData<string> OperatingEndpoints => new()
    {
        "/api/users", "/api/branches", "/api/advisors", "/api/channels", "/api/conversations", "/api/metrics/summary",
    };

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public TenantEnforcementTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private static async Task AssertNotActiveAsync(HttpResponseMessage response, TenantStatus status)
    {
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("tenant_not_active", body.RootElement.GetProperty("code").GetString());
        Assert.Equal(status.ToString(), body.RootElement.GetProperty("tenantStatus").GetString());
        Assert.Equal(status == TenantStatus.Suspended ? "Tu empresa está suspendida." : "Tu empresa está pendiente de activación.", body.RootElement.GetProperty("detail").GetString());
    }

    private Task SetStatusAsync(TestAdmin admin, Guid tenantId, string status)
        => admin.Client.PutAsJsonAsync($"{AdminTenantsUrl}/{tenantId}/status", new { status }).ContinueWith(t => Assert.Equal(HttpStatusCode.OK, t.Result.StatusCode));

    [Theory]
    [MemberData(nameof(OperatingEndpoints))]
    public async Task APendingCompany_IsRefusedOnEveryOperatingEndpoint_EvenForItsOwner(string url)
    {
        using var tenant = await StartTenantAsync(_factory, _client, "pending", pending: true);

        await AssertNotActiveAsync(await tenant.Owner.Client.GetAsync(url), TenantStatus.Pending);
    }

    [Fact]
    public async Task APendingCompany_CannotWrite_AndAMemberWithoutPermissionGetsTheSameCode()
    {
        using var tenant = await StartTenantAsync(_factory, _client, "pending", pending: true);
        using var member = await tenant.AddMemberAsync("plain");

        await AssertNotActiveAsync(await PostBranchAsync(tenant.Owner.Client, "Sede"), TenantStatus.Pending);
        await AssertNotActiveAsync(await CreateUserAsync(tenant.Owner.Client, UniqueEmail("x"), StrongPassword), TenantStatus.Pending);
        await AssertNotActiveAsync(await member.Client.GetAsync("/api/branches"), TenantStatus.Pending); // would be 403 for lack of permission anyway
    }

    [Fact]
    public async Task APendingCompany_CanStillSignIn_ReadItself_AndReadItsProfile()
    {
        using var tenant = await StartTenantAsync(_factory, _client, "pending", pending: true);

        var login = await _client.PostAsJsonAsync("/api/auth/login", new { email = tenant.Session.OwnerEmail, password = StrongPassword });
        var info = await tenant.Owner.Client.GetFromJsonAsync<TenantInfoResponse>("/api/tenant");
        var me = await tenant.Owner.Client.GetAsync("/api/users/me");

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Equal(TenantStatus.Pending, info!.Status);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        // Login with a token already in hand (a signed-in pending user logging in again) is still not an operating endpoint.
        using var withToken = AuthorizedClient(_factory, tenant.Owner.Token);
        Assert.Equal(HttpStatusCode.OK, (await withToken.PostAsJsonAsync("/api/auth/login", new { email = tenant.Session.OwnerEmail, password = StrongPassword })).StatusCode);
    }

    [Fact]
    public async Task Suspending_CutsOffTheAlreadyIssuedTokenAtOnce_AndReactivatingRestoresIt()
    {
        using var admin = await StartAdminAsync(_factory, _client);
        using var tenant = await StartTenantAsync(_factory, _client, "suspend");
        Assert.Equal(HttpStatusCode.OK, (await tenant.Owner.Client.GetAsync("/api/branches")).StatusCode);

        await SetStatusAsync(admin, tenant.TenantId, "Suspended");
        await AssertNotActiveAsync(await tenant.Owner.Client.GetAsync("/api/branches"), TenantStatus.Suspended);
        Assert.Equal(TenantStatus.Suspended, (await tenant.Owner.Client.GetFromJsonAsync<TenantInfoResponse>("/api/tenant"))!.Status);

        await SetStatusAsync(admin, tenant.TenantId, "Active");
        Assert.Equal(HttpStatusCode.OK, (await tenant.Owner.Client.GetAsync("/api/branches")).StatusCode);
    }

    [Fact]
    public async Task ThePendingCompany_BecomesOperational_WhenTheAdministratorActivatesIt_WithItsQuota()
    {
        using var admin = await StartAdminAsync(_factory, _client);
        using var tenant = await StartTenantAsync(_factory, _client, "approve", pending: true);

        await ActivateTenantAsync(admin, tenant.TenantId, 2);

        var branch = await CreateBranchAsync(tenant.Owner.Client);
        var line = await CreateChannelAsync(tenant.Owner.Client, branch.Id);
        Assert.True(line.IsActive);
        var info = await tenant.Owner.Client.GetFromJsonAsync<TenantInfoResponse>("/api/tenant");
        Assert.Equal((TenantStatus.Active, 2, 1, 1), (info!.Status, info.MaxActiveLines, info.ActiveLines, info.RegisteredLines));
    }

    [Fact]
    public async Task TheWebhookOfANonActiveCompany_Is401AndStoresNothing_AndWorksAgainWhenActivated()
    {
        using var admin = await StartAdminAsync(_factory, _client);
        using var f = await StartChannelAsync(_factory, _client);
        await ClearQueueAsync(_factory.Services);
        var payload = Dialog360Samples.InboundText($"wamid.EN-{Guid.NewGuid():N}");
        using var stranger = await PostWebhookAsync(_client, f.Path, "wrong-secret", payload);

        await SetStatusAsync(admin, f.TenantId, "Suspended");
        using var suspended = await PostWebhookAsync(_client, f.Path, f.Secret, payload);
        var stored = (await SnapshotAsync(_factory.Services, f.TenantId)).Events.Count;
        await SetStatusAsync(admin, f.TenantId, "Active");
        using var active = await PostWebhookAsync(_client, f.Path, f.Secret, payload);

        Assert.Equal(HttpStatusCode.Unauthorized, suspended.StatusCode);
        Assert.Equal((await ReadProblemJsonAsync(stranger)).Detail, (await ReadProblemJsonAsync(suspended)).Detail); // the answer to a wrong secret
        Assert.Equal(0, stored);
        Assert.Equal(HttpStatusCode.OK, active.StatusCode);
        Assert.Single((await SnapshotAsync(_factory.Services, f.TenantId)).Events);
    }

    [Fact]
    public async Task TheWebhookRunner_LeavesTheEventsOfASuspendedCompanyPending_AndProcessesThemOnReactivation()
    {
        using var admin = await StartAdminAsync(_factory, _client);
        using var f = await StartChannelAsync(_factory, _client);
        await ClearQueueAsync(_factory.Services);
        await DeliverAsync(_client, f, Dialog360Samples.InboundText());

        await SetStatusAsync(admin, f.TenantId, "Suspended");
        await DrainAsync(RunnerOf(_factory.Services));
        var whileSuspended = await SnapshotAsync(_factory.Services, f.TenantId);

        await SetStatusAsync(admin, f.TenantId, "Active");
        await DrainAsync(RunnerOf(_factory.Services));
        var afterwards = await SnapshotAsync(_factory.Services, f.TenantId);

        Assert.Equal(WebhookEventStatus.Pending, Assert.Single(whileSuspended.Events).Status);
        Assert.Empty(whileSuspended.Messages);
        Assert.Equal(WebhookEventStatus.Processed, Assert.Single(afterwards.Events).Status);
        Assert.Single(afterwards.Messages);
    }

    [Fact]
    public async Task TheQueueMonitor_DoesNotCountTheWaitingEventsOfASuspendedCompanyAsABacklog()
    {
        using var admin = await StartAdminAsync(_factory, _client);
        using var f = await StartChannelAsync(_factory, _client);
        await ClearQueueAsync(_factory.Services);
        await DeliverAsync(_client, f, Dialog360Samples.InboundText());
        var monitor = _factory.Services.GetService(typeof(Conversia.Infrastructure.Webhooks.IWebhookQueueMonitor)) as Conversia.Infrastructure.Webhooks.IWebhookQueueMonitor;
        Assert.NotNull(monitor);

        var active = await monitor!.GetSnapshotAsync();
        await SetStatusAsync(admin, f.TenantId, "Suspended");
        var suspended = await monitor.GetSnapshotAsync();

        Assert.Equal(1, active.PendingCount);
        Assert.Equal(0, suspended.PendingCount);
    }
}
