using System.Net;
using System.Net.Http.Json;
using Conversia.Application.DTOs.Organization;
using Conversia.Domain.Enums;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>
/// <c>PUT</c>/<c>DELETE /api/channels/{id}/provider-credentials</c>: the provider API key goes in, is stored encrypted, and never comes
/// back out through any endpoint.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class ProviderCredentialsApiTests
{
    private const string ApiKey = "d360-SENSITIVE-key-Zx9";

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly MessagingHost _host;

    public ProviderCredentialsApiTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _host = MessagingHostOf(factory);
    }

    [Fact]
    public async Task SettingTheKey_StoresItEncrypted_AndNoAnswerEverCarriesIt()
    {
        using var f = await StartChannelAsync(_factory, _client);
        using var owner = _host.ClientOf(f.Tenant.Owner);

        var response = await PutCredentialsAsync(owner, f.Channel.Id, $"  {ApiKey}  ", "Production");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var put = await response.Content.ReadAsStringAsync();
        var channel = System.Text.Json.JsonSerializer.Deserialize<ChannelResponse>(put, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        Assert.True(channel.HasProviderApiKey);
        Assert.NotNull(channel.ProviderApiKeySetAt);
        Assert.Equal(nameof(WhatsAppProviderEnvironment.Production), channel.ProviderEnvironment);

        var stored = await LoadChannelAsync(_factory.Services, f.Channel.Id);
        Assert.StartsWith("v1.", stored.ProviderApiKeyCiphertext, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKey, stored.ProviderApiKeyCiphertext);
        Assert.Equal(WhatsAppProviderEnvironment.Production, stored.ProviderEnvironment);

        var everything = put
            + await owner.GetStringAsync($"{ChannelsUrl}/{f.Channel.Id}")
            + await owner.GetStringAsync(ChannelsUrl)
            + await (await owner.PostAsync($"{ChannelsUrl}/{f.Channel.Id}/rotate-secret", null)).Content.ReadAsStringAsync()
            + await (await owner.PostAsync($"{ChannelsUrl}/{f.Channel.Id}/deactivate", null)).Content.ReadAsStringAsync();
        Assert.DoesNotContain(ApiKey, everything);
        Assert.DoesNotContain(stored.ProviderApiKeyCiphertext!, everything);
        Assert.DoesNotContain("ciphertext", everything, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"hasProviderApiKey\":true", everything);
    }

    [Fact]
    public async Task WithoutAKey_OnlyTheEnvironmentChanges_AndAChannelThatHasNoneIsRefused()
    {
        using var f = await StartChannelAsync(_factory, _client);
        using var owner = _host.ClientOf(f.Tenant.Owner);

        Assert.Equal(HttpStatusCode.BadRequest, (await PutCredentialsAsync(owner, f.Channel.Id, null)).StatusCode);

        await SetCredentialsAsync(owner, f.Channel.Id, ApiKey);
        var before = await LoadChannelAsync(_factory.Services, f.Channel.Id);

        var response = await PutCredentialsAsync(owner, f.Channel.Id, "", "Production");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var after = await LoadChannelAsync(_factory.Services, f.Channel.Id);
        Assert.Equal(before.ProviderApiKeyCiphertext, after.ProviderApiKeyCiphertext);
        Assert.Equal(before.ProviderApiKeySetAt, after.ProviderApiKeySetAt);
        Assert.Equal(WhatsAppProviderEnvironment.Production, after.ProviderEnvironment);
    }

    [Fact]
    public async Task InvalidInput_IsA400UnderItsField_ThatNeverRepeatsTheKey()
    {
        using var f = await StartChannelAsync(_factory, _client);
        using var owner = _host.ClientOf(f.Tenant.Owner);

        var badKey = await PutCredentialsAsync(owner, f.Channel.Id, "two words SENSITIVE-9911");
        Assert.DoesNotContain("SENSITIVE-9911", await badKey.Content.ReadAsStringAsync());
        await AssertValidationErrorUnderAsync(badKey, "ApiKey");

        await AssertValidationErrorUnderAsync(await PutCredentialsAsync(owner, f.Channel.Id, ApiKey, "Staging"), "Environment");
        await AssertValidationErrorUnderAsync(await PutCredentialsAsync(owner, f.Channel.Id, ApiKey, null), "Environment");
        // A URL is not an environment: the base URLs come from configuration only.
        await AssertValidationErrorUnderAsync(await PutCredentialsAsync(owner, f.Channel.Id, ApiKey, "https://attacker.example"), "Environment");

        Assert.False((await LoadChannelAsync(_factory.Services, f.Channel.Id)).HasProviderApiKey);
    }

    [Fact]
    public async Task Clearing_ForgetsTheKey_AndIsIdempotent()
    {
        using var f = await StartChannelAsync(_factory, _client);
        using var owner = _host.ClientOf(f.Tenant.Owner);
        await SetCredentialsAsync(owner, f.Channel.Id, ApiKey);

        var first = await owner.DeleteAsync(CredentialsUrl(f.Channel.Id));
        var second = await owner.DeleteAsync(CredentialsUrl(f.Channel.Id));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.False((await ReadBodyAsync<ChannelResponse>(second)).HasProviderApiKey);
        var stored = await LoadChannelAsync(_factory.Services, f.Channel.Id);
        Assert.Null(stored.ProviderApiKeyCiphertext);
        Assert.Null(stored.ProviderApiKeySetAt);
    }

    [Fact]
    public async Task OnlyThoseWhoManageTheOrganization_MayChangeTheCredentials()
    {
        using var f = await StartChannelAsync(_factory, _client);
        using var viewerActor = await f.Tenant.AddMemberAsync("viewer", Permission.OrganizationView, Permission.ConversationsManage);
        using var managerActor = await f.Tenant.AddMemberAsync("manager", Permission.OrganizationManage);
        using var viewer = _host.ClientOf(viewerActor);
        using var manager = _host.ClientOf(managerActor);
        using var anonymous = _host.ClientWith(null);

        Assert.Equal(HttpStatusCode.Unauthorized, (await PutCredentialsAsync(anonymous, f.Channel.Id, ApiKey)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.DeleteAsync(CredentialsUrl(f.Channel.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PutCredentialsAsync(viewer, f.Channel.Id, ApiKey)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.DeleteAsync(CredentialsUrl(f.Channel.Id))).StatusCode);
        Assert.False((await LoadChannelAsync(_factory.Services, f.Channel.Id)).HasProviderApiKey);

        Assert.Equal(HttpStatusCode.OK, (await PutCredentialsAsync(manager, f.Channel.Id, ApiKey)).StatusCode);

        // A viewer learns that a key exists, and nothing else about it.
        var seen = await viewer.GetStringAsync($"{ChannelsUrl}/{f.Channel.Id}");
        Assert.Contains("\"hasProviderApiKey\":true", seen);
        Assert.DoesNotContain(ApiKey, seen);
    }

    [Fact]
    public async Task AnotherTenantsChannel_IsNotFound_AndKeepsItsCredentials()
    {
        using var a = await StartChannelAsync(_factory, _client);
        using var b = await StartChannelAsync(_factory, _client);
        using var ownerA = _host.ClientOf(a.Tenant.Owner);
        using var ownerB = _host.ClientOf(b.Tenant.Owner);
        await SetCredentialsAsync(ownerA, a.Channel.Id, ApiKey);
        var before = await LoadChannelAsync(_factory.Services, a.Channel.Id);

        Assert.Equal(HttpStatusCode.NotFound, (await PutCredentialsAsync(ownerB, a.Channel.Id, "other-key")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ownerB.DeleteAsync(CredentialsUrl(a.Channel.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PutCredentialsAsync(ownerB, Guid.NewGuid(), "other-key")).StatusCode);

        var after = await LoadChannelAsync(_factory.Services, a.Channel.Id);
        Assert.Equal(before.ProviderApiKeyCiphertext, after.ProviderApiKeyCiphertext);
    }

    [Fact]
    public async Task WithoutAMasterKey_TheApiRuns_AndTheCredentialsAnswerUnavailable()
    {
        // The shared host has no Messaging:CredentialEncryptionKey.
        using var f = await StartChannelAsync(_factory, _client);

        var response = await PutCredentialsAsync(f.Tenant.Owner.Client, f.Channel.Id, ApiKey);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var (code, body) = await ReadProblemCodeAsync(response);
        Assert.Equal("provider_credentials_unavailable", code);
        Assert.DoesNotContain(ApiKey, body);
        AssertNoInternalsLeaked(body);
        Assert.False((await LoadChannelAsync(_factory.Services, f.Channel.Id)).HasProviderApiKey);
    }
}
