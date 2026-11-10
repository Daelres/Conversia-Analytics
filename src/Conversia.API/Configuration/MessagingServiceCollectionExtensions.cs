using Conversia.API.RateLimiting;
using Conversia.Application.Interfaces;
using Conversia.Application.Messaging;
using Conversia.Application.Services;
using Conversia.Infrastructure.Messaging;
using Conversia.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Conversia.API.Configuration;

public static class MessagingServiceCollectionExtensions
{
    /// <summary>
    /// Registers outbound messaging: the protector of the stored provider keys, the 360dialog adapter behind
    /// <see cref="IWhatsAppMessageSenderResolver"/>, the send use case and its rate limit. Invalid settings stop the API at startup
    /// naming the key; a missing master key does not (see <see cref="MessagingOptionsValidator"/>). See docs/MESSAGING.md.
    /// </summary>
    public static IServiceCollection AddMessaging(this IServiceCollection services, IConfiguration configuration)
    {
        var protection = MessagingOptionsValidator.GetValidatedSecretProtectionOptions(configuration);
        var dialog360 = MessagingOptionsValidator.GetValidatedDialog360ApiOptions(configuration);
        var sending = MessagingOptionsValidator.GetValidatedSendingOptions(configuration);

        // The key lives in the protector from here on; the configuration must not keep a second copy.
        configuration[Infrastructure.Configuration.SecretProtectionOptions.KeyKey] = null;

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(dialog360);
        services.AddSingleton(sending);
        services.AddSingleton<ISecretProtector>(new AesGcmSecretProtector(protection));
        services.AddMessageSendRateLimiting(sending);

        // One attempt, a fixed timeout, no redirects (the key would follow them) and no handler logging (it can print headers).
        services.AddHttpClient<Dialog360MessageSender>(client =>
            {
                client.Timeout = dialog360.Timeout;
                client.MaxResponseContentBufferSize = Dialog360MessageSender.MaxResponseBytes;
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                ConnectTimeout = dialog360.Timeout,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            })
            .RemoveAllLoggers();

        services.AddScoped<IWhatsAppMessageSenderResolver, WhatsAppMessageSenderResolver>();
        services.AddScoped<IMessageSendingService, MessageSendingService>();
        return services;
    }
}
