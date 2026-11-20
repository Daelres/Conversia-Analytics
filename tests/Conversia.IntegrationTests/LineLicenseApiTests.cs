using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Conversia.Application.DTOs;
using Conversia.Application.DTOs.Admin;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>
/// The platform's 360dialog line licence inventory (docs/PLATFORM_ADMIN.md, "Line licences"): create, assign, release, retire, audited and
/// masked; linked to the company's line; and, with <c>Licensing:RequireAssignedLicense</c>, required to switch a line on.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class LineLicenseApiTests
{
    private const string LicensesUrl = "/api/admin/licenses";
    private static long _number = 3_100_000_000L + (DateTime.UtcNow.Ticks % 10_000_000L) * 10;

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public LineLicenseApiTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    /// <summary>A Colombian mobile number no other licence of the shared database has (the inventory allows one live licence per number).</summary>
    private static string NewNumber()
    {
        var digits = Interlocked.Increment(ref _number).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return $"+57 {digits[..3]} {digits[3..6]} {digits[6..]}";
    }

    private static async Task<LineLicenseResponse> CreateAsync(HttpClient admin, string number, decimal cost = 12.5m, bool customerOwned = false)
    {
        var response = await admin.PostAsJsonAsync(LicensesUrl, new { phoneNumber = number, providerChannelId = "ch-" + Guid.NewGuid().ToString("N")[..8], monthlyCostUsd = cost, customerOwned });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<LineLicenseResponse>())!;
    }

    private static async Task<string> CodeOf(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("code").GetString()!;
    }

    private WebApplicationFactory<Program> RequiredHost()
        => _factory.GetOrAddHost("licensing:required", () => _factory.WithWebHostBuilder(b => b.UseSetting("Licensing:RequireAssignedLicense", "true")));

    [Fact]
    public async Task ALicence_IsCreatedAvailable_AndShownAndAuditedOnlyMasked()
    {
        using var admin = await StartAdminAsync(_factory, _client);
        var number = NewNumber();

        var created = await CreateAsync(admin.Client, number);

        var digits = new string([.. number.Where(char.IsAsciiDigit)]);
        Assert.Equal(("Available", "360dialog", "•••• " + digits[^4..], 12.5m), (created.Status, created.Provider, created.PhoneNumberMasked, created.MonthlyCostUsd));
        var bodies = new[]
        {
            await admin.Client.GetStringAsync($"{LicensesUrl}/{created.Id}"),
            await admin.Client.GetStringAsync($"{LicensesUrl}?status=Available&pageSize=100"),
            await admin.Client.GetStringAsync("/api/admin/audit?pageSize=100"),
        };
        Assert.All(bodies, body => Assert.DoesNotContain(digits, body));
        Assert.All(bodies, body => Assert.DoesNotContain(digits[..6], body));
        Assert.Contains("license.created", bodies[2]);
    }

    [Fact]
    public async Task OneLiveLicencePerNumber_ACustomerOwnedOneCostsNothing_AndBadInputIsRefused()
    {
        using var admin = await StartAdminAsync(_factory, _client);
        var number = NewNumber();
        await CreateAsync(admin.Client, number);

        var duplicate = await admin.Client.PostAsJsonAsync(LicensesUrl, new { phoneNumber = number, monthlyCostUsd = 1 });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("license_duplicate", await CodeOf(duplicate));

        Assert.Equal(0m, (await CreateAsync(admin.Client, NewNumber(), cost: 0m, customerOwned: true)).MonthlyCostUsd);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.Client.PostAsJsonAsync(LicensesUrl, new { phoneNumber = NewNumber(), monthlyCostUsd = 5, customerOwned = true })).StatusCode);
        await AssertValidationErrorUnderAsync(await admin.Client.PostAsJsonAsync(LicensesUrl, new { phoneNumber = NewNumber() }), "MonthlyCostUsd");
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.Client.PostAsJsonAsync(LicensesUrl, new { phoneNumber = "12", monthlyCostUsd = 1 })).StatusCode);
    }

    [Fact]
    public async Task AssigningLinksTheCompanysLineWithThatNumber_ReleasingAndRetiringFollowTheLifecycle()
    {
        using var admin = await StartAdminAsync(_factory, _client);
        var number = NewNumber();
        using var f = await StartChannelAsync(_factory, _client, phone: number);
        var license = await CreateAsync(admin.Client, number);

        var assigned = await admin.Client.PostAsJsonAsync($"{LicensesUrl}/{license.Id}/assign", new { tenantId = f.TenantId });
        Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);
        var body = (await assigned.Content.ReadFromJsonAsync<LineLicenseResponse>())!;
        Assert.Equal(("Assigned", (Guid?)f.TenantId, (Guid?)f.Channel.Id), (body.Status, body.AssignedTenantId, body.ChannelId));
        Assert.False(string.IsNullOrEmpty(body.AssignedTenantName));

        var detail = (await admin.Client.GetFromJsonAsync<TenantDetailResponse>($"{AdminTenantsUrl}/{f.TenantId}"))!;
        Assert.Equal(license.Id, Assert.Single(detail.Licenses).Id);

        var again = await admin.Client.PostAsJsonAsync($"{LicensesUrl}/{license.Id}/assign", new { tenantId = f.TenantId });
        Assert.Equal("license_not_available", await CodeOf(again));
        var retireAssigned = await admin.Client.PostAsync($"{LicensesUrl}/{license.Id}/retire", null);
        Assert.Equal((HttpStatusCode.Conflict, "license_assigned"), (retireAssigned.StatusCode, await CodeOf(retireAssigned)));

        Assert.Equal(HttpStatusCode.OK, (await admin.Client.PostAsync($"{LicensesUrl}/{license.Id}/release", null)).StatusCode);
        Assert.True((await LoadChannelAsync(_factory.Services, f.Channel.Id)).IsActive); // licences are not required in this deployment
        var retired = await admin.Client.PostAsync($"{LicensesUrl}/{license.Id}/retire", null);
        Assert.Equal("Retired", (await retired.Content.ReadFromJsonAsync<LineLicenseResponse>())!.Status);

        var audit = (await admin.Client.GetFromJsonAsync<PagedResponse<AuditEntryResponse>>($"/api/admin/audit?tenantId={f.TenantId}"))!.Items.Select(e => e.Action).ToList();
        Assert.Contains("license.assigned", audit);
        Assert.Contains("license.released", audit);

        // A retired number can be licensed again.
        await CreateAsync(admin.Client, number);
    }

    [Fact]
    public async Task UnknownLicencesAndCompanies_AreNotFound_AndACompanyCannotUseTheInventory()
    {
        using var admin = await StartAdminAsync(_factory, _client);
        using var tenant = await StartTenantAsync(_factory, _client);
        var license = await CreateAsync(admin.Client, NewNumber());

        Assert.Equal(HttpStatusCode.NotFound, (await admin.Client.GetAsync($"{LicensesUrl}/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.Client.PostAsJsonAsync($"{LicensesUrl}/{license.Id}/assign", new { tenantId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await tenant.Owner.Client.GetAsync(LicensesUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await tenant.Owner.Client.PostAsJsonAsync($"{LicensesUrl}/{license.Id}/assign", new { tenantId = tenant.TenantId })).StatusCode);
    }

    [Fact]
    public async Task WhenALicenceIsRequired_ALineIsSwitchedOnOnlyWithItsLicence_AndReleasingItSwitchesTheLineOff()
    {
        var host = RequiredHost();
        using var admin = await StartAdminAsync(_factory, _client);
        using var adminOnHost = host.CreateClient();
        adminOnHost.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", admin.Token);
        using var tenant = await StartTenantAsync(_factory, _client);
        using var owner = host.CreateClient();
        owner.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tenant.Owner.Token);
        var number = NewNumber();
        var branch = await CreateBranchAsync(owner);
        var line = await CreateInactiveChannelAsync(owner, branch.Id, number);

        var refused = await owner.PostAsync($"{ChannelsUrl}/{line.Id}/activate", null);
        Assert.Equal((HttpStatusCode.Conflict, "license_required"), (refused.StatusCode, await CodeOf(refused)));
        Assert.Contains("licencia", await refused.Content.ReadAsStringAsync());

        var license = await CreateAsync(adminOnHost, number);
        Assert.Equal(HttpStatusCode.OK, (await adminOnHost.PostAsJsonAsync($"{LicensesUrl}/{license.Id}/assign", new { tenantId = tenant.TenantId })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"{ChannelsUrl}/{line.Id}/activate", null)).StatusCode);
        Assert.Equal(line.Id, (await adminOnHost.GetFromJsonAsync<LineLicenseResponse>($"{LicensesUrl}/{license.Id}"))!.ChannelId);

        Assert.Equal(HttpStatusCode.OK, (await adminOnHost.PostAsync($"{LicensesUrl}/{license.Id}/release", null)).StatusCode);
        Assert.False((await LoadChannelAsync(_factory.Services, line.Id)).IsActive);
        var audit = (await adminOnHost.GetFromJsonAsync<PagedResponse<AuditEntryResponse>>($"/api/admin/audit?tenantId={tenant.TenantId}"))!.Items;
        Assert.True(audit.First(e => e.Action == "license.released").Details.GetProperty("lineDeactivated").GetBoolean());
    }

    [Fact]
    public async Task RegisteringALineWithAnAssignedNumber_LinksTheLicence()
    {
        using var admin = await StartAdminAsync(_factory, _client);
        using var tenant = await StartTenantAsync(_factory, _client);
        var number = NewNumber();
        var license = await CreateAsync(admin.Client, number);
        Assert.Equal(HttpStatusCode.OK, (await admin.Client.PostAsJsonAsync($"{LicensesUrl}/{license.Id}/assign", new { tenantId = tenant.TenantId })).StatusCode);
        var branch = await CreateBranchAsync(tenant.Owner.Client);

        var line = await CreateInactiveChannelAsync(tenant.Owner.Client, branch.Id, number);

        Assert.Equal(line.Id, (await admin.Client.GetFromJsonAsync<LineLicenseResponse>($"{LicensesUrl}/{license.Id}"))!.ChannelId);
    }
}
