using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Conversia.Application.Messaging;
using Conversia.Domain.Entities;
using Conversia.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Conversia.IntegrationTests;

/// <summary>Helpers of the messaging integration tests: a host with a master key and a stand-in provider, so no test ever calls 360dialog.</summary>
internal static partial class TestHelpers
{
    internal const string EncryptionKeySetting = "Messaging:CredentialEncryptionKey";

    /// <summary>A throw-away master key, new on every run.</summary>
    internal static readonly string TestEncryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    /// <summary>Stands in for the provider. What it answers is set per channel, so tests that share the host do not disturb each other.</summary>
    internal sealed class FakeMessageSender : IWhatsAppMessageSender, IWhatsAppMessageSenderResolver
    {
        private readonly ConcurrentDictionary<Guid, Func<WhatsAppSendResult>> _answers = new();
        private readonly ConcurrentQueue<(Guid ChannelId, string RecipientWaId, string Text, string? ProviderMessageId)> _sent = new();

        public void Answer(Guid channelId, Func<WhatsAppSendResult> answer) => _answers[channelId] = answer;

        public IReadOnlyList<(Guid ChannelId, string RecipientWaId, string Text, string? ProviderMessageId)> SentBy(Guid channelId)
            => _sent.Where(s => s.ChannelId == channelId).ToList();

        public IWhatsAppMessageSender Resolve(WhatsAppChannel channel) => this;

        public Task<WhatsAppSendResult> SendTextAsync(WhatsAppTextMessage message, CancellationToken cancellationToken = default)
        {
            var result = _answers.TryGetValue(message.Channel.Id, out var answer) ? answer() : WhatsAppSendResult.Sent($"wamid.FAKE-{Guid.NewGuid():N}");
            _sent.Enqueue((message.Channel.Id, message.RecipientWaId, message.Text, result.ProviderMessageId));
            return Task.FromResult(result);
        }
    }

    internal sealed record MessagingHost(WebApplicationFactory<Program> Host, FakeMessageSender Sender)
    {
        public HttpClient ClientOf(TestActor actor) => ClientWith(actor.Token);

        public HttpClient ClientWith(string? token)
        {
            var client = Host.CreateClient();
            if (token is not null)
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            return client;
        }
    }

    /// <summary>The shared database behind a host that has a master key and whose provider is <see cref="FakeMessageSender"/>.</summary>
    internal static MessagingHost MessagingHostOf(CustomWebApplicationFactory factory, string name = "messaging", Dictionary<string, string>? settings = null)
        => factory.GetOrAddHost(name, () =>
        {
            var sender = new FakeMessageSender();
            var host = factory.WithWebHostBuilder(builder =>
            {
                builder.UseSetting(EncryptionKeySetting, TestEncryptionKey);
                foreach (var (key, value) in settings ?? [])
                {
                    builder.UseSetting(key, value);
                }

                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IWhatsAppMessageSenderResolver>();
                    services.AddSingleton<IWhatsAppMessageSenderResolver>(sender);
                });
            });
            return new MessagingHost(host, sender);
        });

    internal static string CredentialsUrl(Guid channelId) => $"{ChannelsUrl}/{channelId}/provider-credentials";

    internal static string SendUrl(Guid conversationId) => $"/api/conversations/{conversationId}/messages";

    internal static Task<HttpResponseMessage> PutCredentialsAsync(HttpClient client, Guid channelId, string? apiKey, string? environment = "Sandbox")
        => client.PutAsJsonAsync(CredentialsUrl(channelId), new { apiKey, environment });

    internal static async Task SetCredentialsAsync(HttpClient client, Guid channelId, string apiKey = "d360-test-key-Ab3")
        => Assert.Equal(HttpStatusCode.OK, (await PutCredentialsAsync(client, channelId, apiKey)).StatusCode);

    internal static Task<HttpResponseMessage> SendMessageAsync(HttpClient client, Guid conversationId, string? text = "Hola, con gusto te ayudo.")
        => client.PostAsJsonAsync(SendUrl(conversationId), new { text });

    /// <summary>The stored channel, bypassing the tenant filter.</summary>
    internal static async Task<WhatsAppChannel> LoadChannelAsync(IServiceProvider services, Guid channelId)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.WhatsAppChannels.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == channelId);
    }

    /// <summary>The <c>code</c> extension of a problem body, and the body itself for leak checks.</summary>
    internal static async Task<(string? Code, string Body)> ReadProblemCodeAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        using var json = System.Text.Json.JsonDocument.Parse(body);
        return (json.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null, body);
    }
}
