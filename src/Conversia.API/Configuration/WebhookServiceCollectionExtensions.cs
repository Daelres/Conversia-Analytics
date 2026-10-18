using Conversia.API.HostedServices;
using Conversia.API.RateLimiting;
using Conversia.Application.Ingestion;
using Conversia.Application.Interfaces;
using Conversia.Application.Services;
using Conversia.Domain.Interfaces;
using Conversia.Infrastructure.Configuration;
using Conversia.Infrastructure.Repositories;
using Conversia.Infrastructure.Webhooks;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Conversia.API.Configuration;

public static class WebhookServiceCollectionExtensions
{
    /// <summary>
    /// Registers the 360dialog webhook (endpoint options, rate limit, ingestion service) and the background worker that processes the
    /// stored events. With <c>Webhooks:Processing:Enabled=false</c> the worker is not started (events still pile up as Pending).
    /// Also binds <c>Dialog360:ApiKey</c>, which nothing uses yet.
    /// </summary>
    public static IServiceCollection AddWebhookIngestion(this IServiceCollection services, IConfiguration configuration)
    {
        var endpoint = WebhookOptionsValidator.GetValidatedEndpointOptions(configuration);
        var processing = WebhookOptionsValidator.GetValidatedProcessingOptions(configuration);
        var retention = WebhookOptionsValidator.GetValidatedRetentionOptions(configuration);
        var health = WebhookOptionsValidator.GetValidatedHealthOptions(configuration);

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(endpoint);
        services.AddSingleton(processing);
        services.AddSingleton(retention);
        services.AddSingleton(health);
        services.AddSingleton(configuration.GetSection(Dialog360Options.SectionName).Get<Dialog360Options>() ?? new Dialog360Options());
        services.AddSingleton<WebhookRateLimiter>();

        services.AddScoped<IWebhookEventRepository, WebhookEventRepository>();
        services.AddScoped<IContactRepository, ContactRepository>();
        services.AddScoped<IConversationRepository, ConversationRepository>();
        services.AddScoped<IMessageRepository, MessageRepository>();
        services.AddScoped<IWebhookIngestionService, WebhookIngestionService>();
        services.AddScoped<IWebhookEventProcessor, WebhookEventProcessor>();
        services.AddSingleton(new WebhookIngestionLimits(processing.MaxMessagesPerDelivery));
        services.AddSingleton<WebhookEventRunner>();
        services.AddScoped<IWebhookEventMaintenance, WebhookEventMaintenance>();
        services.AddSingleton<IWebhookQueueMonitor, WebhookQueueMonitor>();
        services.AddSingleton<WebhookQueueBacklogReporter>();
        services.AddScoped<WebhookEventPurger>();
        services.AddHealthChecks().AddCheck<WebhookQueueHealthCheck>("webhook-queue");

        if (retention.Enabled)
        {
            services.AddHostedService<WebhookEventRetentionService>();
        }

        if (processing.Enabled)
        {
            services.AddHostedService<WebhookEventProcessingService>();
        }

        return services;
    }
}
