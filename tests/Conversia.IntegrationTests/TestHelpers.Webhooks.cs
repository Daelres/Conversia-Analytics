using System.Text;
using Conversia.Application.DTOs.Organization;
using Conversia.Domain.Entities;
using Conversia.Infrastructure.Persistence;
using Conversia.Infrastructure.Webhooks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Conversia.Tests.Shared;

namespace Conversia.IntegrationTests;

/// <summary>Helpers shared by the webhook ingestion integration test classes.</summary>
internal static partial class TestHelpers
{
    /// <summary>A tenant with one branch and one WhatsApp channel, and the secret of the channel (known only here, as in real life).</summary>
    internal sealed record ChannelFixture(ProductTenant Tenant, BranchResponse Branch, CreateChannelResponse Channel) : IDisposable
    {
        public void Dispose() => Tenant.Dispose();

        public string Path => Channel.WebhookPath;

        public string Secret => Channel.WebhookSecret;

        public Guid TenantId => Tenant.TenantId;
    }

    internal static async Task<ChannelFixture> StartChannelAsync(
        CustomWebApplicationFactory factory, HttpClient anonymous, string? providerPhoneNumberId = Dialog360Samples.PhoneNumberId, string phone = "+57 300 555 0100")
    {
        var tenant = await StartProductTenantAsync(factory, anonymous, "hook");
        var branch = await CreateBranchAsync(tenant.Owner.Client);
        var channel = await CreateChannelAsync(tenant.Owner.Client, branch.Id, phone, providerPhoneNumberId);
        return new ChannelFixture(tenant, branch, channel);
    }

    /// <summary>Adds a second channel (another WhatsApp number) to the tenant of <paramref name="fixture"/>.</summary>
    internal static async Task<CreateChannelResponse> AddChannelAsync(ChannelFixture fixture, string? providerPhoneNumberId, string phone = "+57 300 555 0200")
        => await CreateChannelAsync(fixture.Tenant.Owner.Client, fixture.Branch.Id, phone, providerPhoneNumberId);

    internal static Task<HttpResponseMessage> PostWebhookAsync(HttpClient client, string path, string? secret, string body)
        => PostWebhookAsync(client, path, secret, new StringContent(body, Encoding.UTF8, "application/json"));

    internal static Task<HttpResponseMessage> PostWebhookAsync(HttpClient client, string path, string? secret, HttpContent content)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        if (secret is not null)
        {
            request.Headers.Add("X-Webhook-Secret", secret);
        }

        return client.SendAsync(request);
    }

    /// <summary>Delivers a payload the way 360dialog does and asserts the 200.</summary>
    internal static async Task DeliverAsync(HttpClient anonymous, ChannelFixture fixture, string body, string? path = null, string? secret = null)
    {
        using var response = await PostWebhookAsync(anonymous, path ?? fixture.Path, secret ?? fixture.Secret, body);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>Processes every due event, as the worker would, one after the other.</summary>
    internal static async Task DrainAsync(WebhookEventRunner runner)
    {
        while (await runner.ProcessNextAsync())
        {
        }
    }

    internal static WebhookEventRunner RunnerOf(IServiceProvider services) => services.GetRequiredService<WebhookEventRunner>();

    /// <summary>
    /// The queue is shared by every test of the run, so a test that processes events starts from an empty one: whatever earlier tests
    /// left pending is marked processed (its content does not matter to anyone).
    /// </summary>
    internal static async Task ClearQueueAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.ExecuteSqlRawAsync("UPDATE \"WebhookEvents\" SET \"Status\" = 'Processed' WHERE \"Status\" = 'Pending'");
    }

    internal sealed record Snapshot(
        List<Contact> Contacts, List<Conversation> Conversations, List<Message> Messages, List<Opportunity> Opportunities, List<WebhookEvent> Events);

    /// <summary>Everything stored for a tenant, bypassing the tenant filter (the tests assert the filter did its job).</summary>
    internal static async Task<Snapshot> SnapshotAsync(IServiceProvider services, Guid tenantId)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return new Snapshot(
            await db.Contacts.IgnoreQueryFilters().AsNoTracking().Where(e => e.TenantId == tenantId).ToListAsync(),
            await db.Conversations.IgnoreQueryFilters().AsNoTracking().Where(e => e.TenantId == tenantId).ToListAsync(),
            await db.Messages.IgnoreQueryFilters().AsNoTracking().Where(e => e.TenantId == tenantId).OrderBy(e => e.SentAt).ToListAsync(),
            await db.Opportunities.IgnoreQueryFilters().AsNoTracking().Where(e => e.TenantId == tenantId).ToListAsync(),
            await db.WebhookEvents.IgnoreQueryFilters().AsNoTracking().Where(e => e.TenantId == tenantId).OrderBy(e => e.ReceivedAt).ToListAsync());
    }

    internal static DateTime At(long unixSeconds) => DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;

    /// <summary>A host on the same container whose webhook settings are overridden (and optionally whose services are replaced).</summary>
    internal static WebApplicationFactory<Program> WebhookHost(
        CustomWebApplicationFactory factory, Dictionary<string, string> settings, Action<IServiceCollection>? configureServices = null)
        => factory.WithWebHostBuilder(builder =>
        {
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }

            if (configureServices is not null)
            {
                builder.ConfigureServices(configureServices);
            }
        });
}
