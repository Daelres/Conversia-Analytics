using System.Net;
using System.Net.Http.Json;
using Conversia.Application.DTOs.Organization;

namespace Conversia.IntegrationTests;

/// <summary>Helpers shared by the organization (branches, advisors, channels) integration test classes.</summary>
internal static partial class TestHelpers
{
    public const string BranchesUrl = "/api/branches";
    public const string AdvisorsUrl = "/api/advisors";
    public const string ChannelsUrl = "/api/channels";

    public static string UniqueName(string prefix) => $"{prefix} {Guid.NewGuid():N}";

    public static Task<HttpResponseMessage> PostBranchAsync(HttpClient client, string? name)
        => client.PostAsJsonAsync(BranchesUrl, new { name });

    public static Task<HttpResponseMessage> PostAdvisorAsync(HttpClient client, string? name, Guid? branchId, Guid? userId = null)
        => client.PostAsJsonAsync(AdvisorsUrl, new { name, branchId, userId });

    public static Task<HttpResponseMessage> PostChannelAsync(HttpClient client, Guid? branchId, string? displayPhoneNumber = "3001234567", string? providerPhoneNumberId = null)
        => client.PostAsJsonAsync(ChannelsUrl, new { branchId, displayPhoneNumber, providerPhoneNumberId });

    public static async Task<T> ReadBodyAsync<T>(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<T>();
        Assert.NotNull(body);
        return body!;
    }

    public static async Task<BranchResponse> CreateBranchAsync(HttpClient client, string? name = null)
    {
        var response = await PostBranchAsync(client, name ?? UniqueName("Sucursal"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadBodyAsync<BranchResponse>(response);
    }

    public static async Task<AdvisorResponse> CreateAdvisorAsync(HttpClient client, Guid branchId, string? name = null, Guid? userId = null)
    {
        var response = await PostAdvisorAsync(client, name ?? UniqueName("Asesor"), branchId, userId);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadBodyAsync<AdvisorResponse>(response);
    }

    /// <summary>A channel that works: created and then switched on through the real endpoint (a new channel starts inactive).</summary>
    public static async Task<CreateChannelResponse> CreateChannelAsync(HttpClient client, Guid branchId, string phone = "3001234567", string? providerPhoneNumberId = null)
    {
        var created = await CreateInactiveChannelAsync(client, branchId, phone, providerPhoneNumberId);
        var activated = await client.PostAsync($"{ChannelsUrl}/{created.Id}/activate", null);
        Assert.Equal(HttpStatusCode.OK, activated.StatusCode);
        created.IsActive = true;
        return created;
    }

    /// <summary>A registered channel that is switched off (switched off explicitly, so it holds whatever the default of a new channel is).</summary>
    public static async Task<CreateChannelResponse> CreateInactiveChannelAsync(HttpClient client, Guid branchId, string phone = "3001234567", string? providerPhoneNumberId = null)
    {
        var response = await PostChannelAsync(client, branchId, phone, providerPhoneNumberId);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await ReadBodyAsync<CreateChannelResponse>(response);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"{ChannelsUrl}/{created.Id}/deactivate", null)).StatusCode);
        created.IsActive = false;
        return created;
    }
}
