using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Conversia.Application.DTOs;
using Conversia.Application.DTOs.Admin;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>The per-company limits the administrator sets (docs/PLATFORM_ADMIN.md, "Limits"): the AI switch, the AI quota, the send rate limit.</summary>
[Collection(nameof(IntegrationTestCollection))]
public class PlatformAdminLimitsTests
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public PlatformAdminLimitsTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private static string Url(Guid id, string what) => $"{AdminTenantsUrl}/{id}/{what}";

    private static async Task<TenantDetailResponse> DetailAsync(TestAdmin admin, Guid id)
        => (await admin.Client.GetFromJsonAsync<TenantDetailResponse>($"{AdminTenantsUrl}/{id}"))!;

    private static async Task<List<AuditEntryResponse>> AuditAsync(TestAdmin admin, Guid id)
        => (await admin.Client.GetFromJsonAsync<PagedResponse<AuditEntryResponse>>($"/api/admin/audit?tenantId={id}"))!.Items.ToList();

    [Fact]
    public async Task ACompany_StartsWithTheAiOff_AndEveryLimitAtTheDefault()
    {
        using var admin = await StartAdminAsync(_factory, _client);
        using var tenant = await StartTenantAsync(_factory, _client);

        var limits = (await DetailAsync(admin, tenant.TenantId)).Limits;

        Assert.False(limits.AiEnabled);
        Assert.Equal((500, true, 10m), (limits.AiMonthlyConversationQuota, limits.AiQuotaIsDefault, limits.AiMonthlyUsdCeiling));
        Assert.True(limits.SendRateLimitIsDefault);
        Assert.Equal(limits.DefaultSendRateLimitPerMinute, limits.SendRateLimitPerMinute);
    }

    [Fact]
    public async Task SwitchingTheAiOn_NeedsAReasonAndTheTerms_AndIsAuditedAsConsentEvidence()
    {
        using var admin = await StartAdminAsync(_factory, _client);
        using var tenant = await StartTenantAsync(_factory, _client);
        var url = Url(tenant.TenantId, "ai-enabled");

        await AssertValidationErrorUnderAsync(await admin.Client.PutAsJsonAsync(url, new { enabled = true, termsVersion = "v1" }), "Reason");
        var noTerms = await admin.Client.PutAsJsonAsync(url, new { enabled = true, reason = "Contrato" });
        Assert.Equal(HttpStatusCode.BadRequest, noTerms.StatusCode);
        Assert.Contains("términos", await noTerms.Content.ReadAsStringAsync());

        var on = await admin.Client.PutAsJsonAsync(url, new { enabled = true, reason = "Contrato firmado", termsVersion = "ia-2026-11" });
        Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        Assert.True((await on.Content.ReadFromJsonAsync<TenantSummaryResponse>())!.AiEnabled);
        Assert.Equal(HttpStatusCode.OK, (await admin.Client.PutAsJsonAsync(url, new { enabled = true, reason = "otra vez", termsVersion = "ia-2026-11" })).StatusCode);

        var limits = (await DetailAsync(admin, tenant.TenantId)).Limits;
        Assert.Equal((true, "ia-2026-11"), (limits.AiEnabled, limits.AiTermsVersion));
        var entry = Assert.Single(await AuditAsync(admin, tenant.TenantId), e => e.Action == "tenant.ai_enabled_changed"); // the same terms again wrote nothing
        Assert.Equal((false, true), (entry.Details.GetProperty("from").GetBoolean(), entry.Details.GetProperty("to").GetBoolean()));
        Assert.Equal(("Contrato firmado", "ia-2026-11"), (entry.Details.GetProperty("reason").GetString(), entry.Details.GetProperty("termsVersion").GetString()));

        // New terms while it is on: recorded, with the previous version.
        Assert.Equal(HttpStatusCode.OK, (await admin.Client.PutAsJsonAsync(url, new { enabled = true, reason = "Términos nuevos", termsVersion = "ia-2027-01" })).StatusCode);
        var renewal = (await AuditAsync(admin, tenant.TenantId)).Where(e => e.Action == "tenant.ai_enabled_changed").OrderByDescending(e => e.OccurredAt).First();
        Assert.Equal(("ia-2027-01", "ia-2026-11"), (renewal.Details.GetProperty("termsVersion").GetString(), renewal.Details.GetProperty("previousTermsVersion").GetString()));
    }

    [Fact]
    public async Task TheAiQuotaAndTheSendLimit_AreSetAndReset_AuditedAndShownWithTheirDefaults()
    {
        using var admin = await StartAdminAsync(_factory, _client);
        using var tenant = await StartTenantAsync(_factory, _client);

        Assert.Equal(HttpStatusCode.OK, (await admin.Client.PutAsJsonAsync(Url(tenant.TenantId, "ai-quota"), new { monthlyConversations = 1200, reason = "Plan ampliado" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.Client.PutAsJsonAsync(Url(tenant.TenantId, "send-rate-limit"), new { perMinute = 15, reason = "Campaña" })).StatusCode);
        var limits = (await DetailAsync(admin, tenant.TenantId)).Limits;
        Assert.Equal((1200, false, 24m), (limits.AiMonthlyConversationQuota, limits.AiQuotaIsDefault, limits.AiMonthlyUsdCeiling));
        Assert.Equal((15, false), (limits.SendRateLimitPerMinute, limits.SendRateLimitIsDefault));

        Assert.Equal(HttpStatusCode.OK, (await admin.Client.PutAsJsonAsync(Url(tenant.TenantId, "ai-quota"), new { monthlyConversations = (int?)null, reason = "Plan base" })).StatusCode);
        Assert.True((await DetailAsync(admin, tenant.TenantId)).Limits.AiQuotaIsDefault);

        var actions = (await AuditAsync(admin, tenant.TenantId)).Select(e => e.Action).ToList();
        Assert.Equal(2, actions.Count(a => a == "tenant.ai_quota_changed"));
        Assert.Single(actions, a => a == "tenant.send_rate_limit_changed");
    }

    [Fact]
    public async Task LimitsOutOfRange_AreRefused_AndAnUnknownCompanyIsNotFound()
    {
        using var admin = await StartAdminAsync(_factory, _client);
        using var tenant = await StartTenantAsync(_factory, _client);

        await AssertValidationErrorUnderAsync(await admin.Client.PutAsJsonAsync(Url(tenant.TenantId, "ai-quota"), new { monthlyConversations = -1, reason = "x" }), "MonthlyConversations");
        await AssertValidationErrorUnderAsync(await admin.Client.PutAsJsonAsync(Url(tenant.TenantId, "send-rate-limit"), new { perMinute = 0, reason = "x" }), "PerMinute");
        await AssertValidationErrorUnderAsync(await admin.Client.PutAsJsonAsync(Url(tenant.TenantId, "ai-quota"), new { monthlyConversations = 3 }), "Reason");
        await AssertValidationErrorUnderAsync(await admin.Client.PutAsJsonAsync(Url(tenant.TenantId, "send-rate-limit"), new { perMinute = 3 }), "Reason");
        var aboveTheMaximum = await admin.Client.PutAsJsonAsync(Url(tenant.TenantId, "send-rate-limit"), new { perMinute = 1201, reason = "Más" });
        Assert.Equal(HttpStatusCode.BadRequest, aboveTheMaximum.StatusCode);
        Assert.Contains("1200", await aboveTheMaximum.Content.ReadAsStringAsync());
        Assert.Equal(1200, (await DetailAsync(admin, tenant.TenantId)).Limits.MaxSendRateLimitPerMinute);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.Client.PutAsJsonAsync(Url(Guid.NewGuid(), "ai-quota"), new { monthlyConversations = 3, reason = "x" })).StatusCode);
    }

    [Fact]
    public async Task ACompanysToken_CannotChangeItsOwnLimits()
    {
        using var tenant = await StartTenantAsync(_factory, _client);

        foreach (var (what, body) in new (string, object)[]
                 {
                     ("ai-enabled", new { enabled = true, reason = "x", termsVersion = "v" }), ("ai-quota", new { monthlyConversations = 9 }), ("send-rate-limit", new { perMinute = 9 }),
                 })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await tenant.Owner.Client.PutAsJsonAsync(Url(tenant.TenantId, what), body)).StatusCode);
        }
    }

    [Fact]
    public async Task TheCompanysSendLimit_IsWhatTheLimiterEnforces()
    {
        var messaging = MessagingHostOf(_factory);
        using var f = await StartChannelAsync(_factory, _client);
        using var owner = messaging.ClientOf(f.Tenant.Owner);
        await SetCredentialsAsync(owner, f.Channel.Id);
        var seeded = await SeedConversationAsync(_factory.Services, f, DateTime.UtcNow.AddMinutes(-5), [In(0, "Hola")]);
        using var admin = await StartAdminAsync(_factory, _client);
        using var adminOnHost = messaging.ClientWith(admin.Token);

        Assert.Equal(HttpStatusCode.OK, (await adminOnHost.PutAsJsonAsync(Url(f.TenantId, "send-rate-limit"), new { perMinute = 1, reason = "Prueba de límite" })).StatusCode);

        Assert.Equal(HttpStatusCode.Created, (await SendMessageAsync(owner, seeded.ConversationId)).StatusCode);
        var second = await SendMessageAsync(owner, seeded.ConversationId);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);

        // Back to the deployment default (120 a minute in the tests): a fresh window.
        Assert.Equal(HttpStatusCode.OK, (await adminOnHost.PutAsJsonAsync(Url(f.TenantId, "send-rate-limit"), new { perMinute = (int?)null, reason = "Vuelve al valor por defecto" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await SendMessageAsync(owner, seeded.ConversationId)).StatusCode);
    }
}
