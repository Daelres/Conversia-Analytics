using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Conversia.Application.DTOs.Admin;
using Conversia.Application.Interfaces;
using Conversia.Domain.Entities;
using Conversia.Domain.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace Conversia.IntegrationTests;

/// <summary>Helpers for the platform administrator: a signed-in administrator and the calls that approve a company.</summary>
internal static partial class TestHelpers
{
    /// <summary>
    /// Prefix of the name of a tenant the test helpers leave as it registered (Pending). Every other tenant a test registers is made
    /// Active with a generous quota by the test host (<c>ActivateRegisteredTenantsInterceptor</c>), so the many tests of other
    /// features keep working on a tenant that operates, without each repeating the approval.
    /// </summary>
    public const string PendingTenantPrefix = "PENDING ";

    public const string AdminPassword = "Adm1n-Passw0rd-Test!";

    public const string AdminLoginUrl = "/api/admin/auth/login";

    public const string AdminTenantsUrl = "/api/admin/tenants";

    /// <summary>A platform administrator who has signed in through the real endpoint.</summary>
    internal sealed class TestAdmin : IDisposable
    {
        public TestAdmin(CustomWebApplicationFactory factory, Guid adminId, string email, string token)
        {
            AdminId = adminId;
            Email = email;
            Token = token;
            Client = AuthorizedClient(factory, token);
        }

        public Guid AdminId { get; }

        public string Email { get; }

        public string Token { get; }

        public HttpClient Client { get; }

        public void Dispose() => Client.Dispose();
    }

    /// <summary>Creates an administrator straight in the database (there is no endpoint to create one) and signs it in through the login endpoint.</summary>
    internal static async Task<TestAdmin> StartAdminAsync(CustomWebApplicationFactory factory, HttpClient anonymous, string prefix = "admin")
    {
        var email = UniqueEmail(prefix);
        Guid id;
        using (var scope = factory.Services.CreateScope())
        {
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasherService>();
            var admin = PlatformAdmin.Create(email, hasher.HashPassword(AdminPassword));
            Assert.True(await scope.ServiceProvider.GetRequiredService<IPlatformAdminRepository>().TryAddAsync(admin));
            id = admin.Id;
        }

        var response = await anonymous.PostAsJsonAsync(AdminLoginUrl, new { email, password = AdminPassword });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var auth = (await response.Content.ReadFromJsonAsync<AdminAuthResponse>())!;
        return new TestAdmin(factory, id, email, auth.Token);
    }

    /// <summary>Approves a company through the admin API: Active, with the given quota.</summary>
    internal static async Task ActivateTenantAsync(TestAdmin admin, Guid tenantId, int maxActiveLines = 50)
    {
        var status = await admin.Client.PutAsJsonAsync($"{AdminTenantsUrl}/{tenantId}/status", new { status = "Active" });
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        var quota = await admin.Client.PutAsJsonAsync($"{AdminTenantsUrl}/{tenantId}/line-quota", new { maxActiveLines });
        Assert.Equal(HttpStatusCode.OK, quota.StatusCode);
    }

    internal static async Task<TenantSummaryResponse> GetAdminTenantAsync(TestAdmin admin, Guid tenantId)
    {
        var response = await admin.Client.GetAsync($"{AdminTenantsUrl}/{tenantId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadBodyAsync<TenantDetailResponse>(response);
    }

    internal static HttpClient ClientWithToken(CustomWebApplicationFactory factory, string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
