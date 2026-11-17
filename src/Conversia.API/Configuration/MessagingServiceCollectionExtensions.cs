using Conversia.API.RateLimiting;
using Conversia.Application.Interfaces;
using Conversia.Application.Messaging;
using Conversia.Application.Services;
using Conversia.Domain.Interfaces;
using Conversia.Infrastructure.Conversations;
using Conversia.Infrastructure.Messaging;
using Conversia.Infrastructure.Repositories;
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
        services.AddPollingRateLimiting(configuration);

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

        // The template list: same rules as the sender (one attempt per page, no redirects, no handler logging), a bigger answer.
        services.AddHttpClient<IWhatsAppTemplateSource, Dialog360TemplateSource>(client =>
            {
                client.Timeout = dialog360.Timeout;
                client.MaxResponseContentBufferSize = Dialog360TemplateSource.MaxPageBytes;
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                ConnectTimeout = dialog360.Timeout,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            })
            .RemoveAllLoggers();

        services.AddSingleton<SandboxMessageSender>();
        services.AddScoped<IWhatsAppMessageSenderResolver, WhatsAppMessageSenderResolver>();
        services.AddScoped<IMessageSendingService, MessageSendingService>();

        // Templates, read receipts and typing, the inbox (docs/MESSAGING.md).
        services.AddScoped<IMessageTemplateRepository, MessageTemplateRepository>();
        services.AddScoped<ITemplateService, TemplateService>();
        services.AddSingleton<TypingIndicatorThrottle>();
        services.AddScoped<IConversationPresenceService, ConversationPresenceService>();
        services.AddScoped<IInboxQueries, InboxQueries>();
        services.AddScoped<IInboxService, InboxService>();
        services.AddScoped<IMessagingSettingsService, MessagingSettingsService>();

        var templateSync = GetValidatedTemplateSyncOptions(configuration);
        services.AddSingleton(templateSync);
        if (templateSync.Enabled)
        {
            services.AddHostedService<TemplateSyncService>();
        }

        return services;
    }

    public static TemplateSyncOptions GetValidatedTemplateSyncOptions(IConfiguration configuration)
    {
        var defaults = TemplateSyncOptions.Default;
        return new TemplateSyncOptions(
            Enabled: ConfigurationReader.ReadBool(configuration, TemplateSyncOptions.EnabledKey, defaults.Enabled),
            IntervalMinutes: ConfigurationReader.ReadInt(configuration, TemplateSyncOptions.IntervalMinutesKey, defaults.IntervalMinutes, 15, 10080));
    }
}
