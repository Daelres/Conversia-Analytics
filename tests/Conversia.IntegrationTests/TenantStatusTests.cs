using System.Net;
using System.Net.Http.Json;
using Conversia.Application.DTOs.Tenants;
using Conversia.Domain.Enums;
using Conversia.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>A company registers as Pending with no lines, and its own users can read where it stands.</summary>
[Collection(nameof(IntegrationTestCollection))]
public class TenantStatusTests
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public TenantStatusTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task ARegisteredTenant_StartsPendingWithQuotaZero()
    {
        using var tenant = await StartTenantAsync(_factory, _client, "pending");

        using var scope = _factory.Services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Tenants.AsNoTracking().SingleAsync(t => t.Id == tenant.TenantId);

        Assert.Equal(TenantStatus.Pending, stored.Status);
        Assert.Equal(0, stored.MaxActiveLines);
        Assert.Null(stored.StatusChangedBy);
    }

    [Fact]
    public async Task GetTenant_ShowsTheStatusAndTheLineUsage_ToAnySignedInUserOfTheCompany()
    {
        using var tenant = await StartTenantAsync(_factory, _client, "info");
        using var member = await tenant.AddMemberAsync("plain"); // no permission at all

        var asOwner = await tenant.Owner.Client.GetFromJsonAsync<TenantInfoResponse>("/api/tenant");
        var asMember = await member.Client.GetFromJsonAsync<TenantInfoResponse>("/api/tenant");

        Assert.Equal(tenant.TenantId, asOwner!.Id);
        Assert.Equal(TenantStatus.Pending, asOwner.Status);
        Assert.Equal((0, 0, 0), (asOwner.MaxActiveLines, asOwner.ActiveLines, asOwner.RegisteredLines));
        Assert.Equal(asOwner.Id, asMember!.Id);
    }

    [Fact]
    public async Task GetTenant_WithoutAToken_Is401()
        => Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/tenant")).StatusCode);
}
