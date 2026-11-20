using Azure.Identity;
using Azure.Storage.Blobs;
using Conversia.Application.Interfaces;
using Conversia.Application.Media;
using Conversia.Application.Messaging;
using Conversia.Application.Services;
using Conversia.Infrastructure.Configuration;
using Conversia.Infrastructure.Media;
using Conversia.Infrastructure.Messaging;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Conversia.API.Configuration;

/// <summary>The settings of the media store, validated (docs/DOCKER.md, "Media storage"). Secrets are never part of an error message.</summary>
public sealed record MediaStoreSettings(MediaStoreKind Kind, string? LocalRootPath, byte[]? LocalKey, string? AzureConnectionString, Uri? AzureServiceUri, string? AzureManagedIdentityClientId, string AzureContainer);

public static class MediaServiceCollectionExtensions
{
    public const string StoreKey = "Media:Store";
    public const string LocalRootPathKey = "Media:Local:RootPath";
    public const string LocalEncryptionKeyKey = "Media:Local:EncryptionKey";
    public const string AzureConnectionStringKey = "Media:Azure:ConnectionString";
    public const string AzureServiceUriKey = "Media:Azure:ServiceUri";
    public const string AzureManagedIdentityClientIdKey = "Media:Azure:ManagedIdentityClientId";
    public const string AzureContainerKey = "Media:Azure:Container";

    /// <summary>
    /// Registers the media store (<c>Media:Store</c>: <c>None</c>, <c>Local</c> or <c>Azure</c>), the media clients (360dialog and the internal sandbox),
    /// the download and retention jobs and the media use cases (docs/MESSAGING.md, "Media"). Invalid settings stop the API at startup.
    /// </summary>
    public const string LocalAllowOutsideDevelopmentKey = "Media:Local:AllowOutsideDevelopment";

    public static IServiceCollection AddMedia(this IServiceCollection services, IConfiguration configuration, IHostEnvironment? environment = null)
    {
        var options = GetValidatedOptions(configuration);
        var store = GetValidatedStoreSettings(configuration);

        // The local store keeps files on one machine's disk: one instance only, no shared copies. Outside Development it needs an explicit yes
        // (a single-instance installation such as the compose stack); production uses Azure (docs/DOCKER.md, "Media storage").
        if (store.Kind == MediaStoreKind.Local && environment is not null && !environment.IsDevelopment()
            && !ConfigurationReader.ReadBool(configuration, LocalAllowOutsideDevelopmentKey, false))
        {
            throw new InvalidOperationException(
                $"{StoreKey}=Local is for development and single-instance installations: outside Development set {LocalAllowOutsideDevelopmentKey}=true, or use Azure.");
        }
        var dialog360 = MessagingOptionsValidator.GetValidatedDialog360ApiOptions(configuration);

        // The key lives in the store from here on.
        configuration[LocalEncryptionKeyKey] = null;

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(options);
        services.AddSingleton(new MediaSettings(options.Store != MediaStoreKind.None, options.RetentionDays));
        services.AddSingleton<IMediaStore>(sp => store.Kind switch
        {
            MediaStoreKind.Local => new LocalEncryptedMediaStore(store.LocalRootPath!, store.LocalKey!, sp.GetRequiredService<TimeProvider>()),
            MediaStoreKind.Azure => new AzureBlobMediaStore(Container(store), sp.GetRequiredService<TimeProvider>()),
            _ => new UnavailableMediaStore(),
        });

        services.AddHttpClient<Dialog360MediaClient>(client =>
            {
                // The deadline of one download is the client's own (Media:DownloadTimeoutSeconds); this is only the outer bound.
                client.Timeout = TimeSpan.FromSeconds(options.DownloadTimeoutSeconds + 30);
            })
            .AddTypedClient((http, sp) => new Dialog360MediaClient(
                http, sp.GetRequiredService<Application.Interfaces.ISecretProtector>(), dialog360, sp.GetRequiredService<ILogger<Dialog360MediaClient>>(),
                TimeSpan.FromSeconds(options.DownloadTimeoutSeconds)))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false, // the key would follow a redirect
                UseCookies = false,
                ConnectTimeout = dialog360.Timeout,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                AutomaticDecompression = System.Net.DecompressionMethods.All,
            })
            .RemoveAllLoggers();
        services.AddSingleton<SandboxMediaClient>();
        services.TryAddSingleton<IWhatsAppMediaClientResolver>(sp =>
            new WhatsAppMediaClientResolver(sp.GetRequiredService<Dialog360MediaClient>(), sp.GetRequiredService<SandboxMediaClient>()));

        services.AddSingleton<MediaDownloadRunner>();
        services.AddSingleton<MediaRetentionRunner>();
        services.AddSingleton<MediaOrphanSweeper>();
        services.AddScoped<IMessageMediaService, MessageMediaService>();

        // With a store, the jobs always run (the deletion queue is drained whatever the retention setting); Media:JobsEnabled=false stops
        // them all (the integration tests drive them by hand). Without a store nothing runs and the deletion queue is kept for later.
        var downloads = ConfigurationReader.ReadBool(configuration, "Media:DownloadEnabled", options.DownloadEnabled);
        var retention = ConfigurationReader.ReadBool(configuration, "Media:RetentionEnabled", true);
        if (options.Store != MediaStoreKind.None && ConfigurationReader.ReadBool(configuration, "Media:JobsEnabled", true))
        {
            services.AddHostedService(sp => new MediaBackgroundService(
                sp.GetRequiredService<MediaDownloadRunner>(), sp.GetRequiredService<MediaRetentionRunner>(), options, downloads, retention,
                sp.GetRequiredService<ILogger<MediaBackgroundService>>(), sp.GetRequiredService<MediaOrphanSweeper>(), sp.GetRequiredService<TimeProvider>()));
        }

        return services;
    }

    private static BlobContainerClient Container(MediaStoreSettings store)
    {
        if (store.AzureConnectionString is { } connectionString)
        {
            return new BlobContainerClient(connectionString, store.AzureContainer);
        }

        // Managed identity (no secret in configuration); the identity needs "Storage Blob Data Contributor" on the container.
        var credential = store.AzureManagedIdentityClientId is { } clientId
            ? new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(clientId))
            : new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned);
        return new BlobContainerClient(new Uri(store.AzureServiceUri!, store.AzureContainer), credential);
    }

    public static MediaOptions GetValidatedOptions(IConfiguration configuration)
    {
        var d = MediaOptions.Default;
        var storeText = configuration[StoreKey];
        var kind = string.IsNullOrWhiteSpace(storeText) ? MediaStoreKind.None
            : Enum.TryParse<MediaStoreKind>(storeText, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) ? parsed
            : throw new InvalidOperationException($"{StoreKey} must be None, Local or Azure.");
        return new MediaOptions(
            kind,
            RetentionDays: ConfigurationReader.ReadInt(configuration, "Media:RetentionDays", d.RetentionDays, 1, Domain.Entities.Tenant.MaxMediaRetentionDays),
            DownloadEnabled: ConfigurationReader.ReadBool(configuration, "Media:DownloadEnabled", d.DownloadEnabled),
            DownloadIntervalSeconds: ConfigurationReader.ReadInt(configuration, "Media:DownloadIntervalSeconds", d.DownloadIntervalSeconds, 1, 3600),
            DownloadWindowHours: ConfigurationReader.ReadInt(configuration, "Media:DownloadWindowHours", d.DownloadWindowHours, 1, 167),
            MaxDownloadAttempts: ConfigurationReader.ReadInt(configuration, "Media:MaxDownloadAttempts", d.MaxDownloadAttempts, 1, 50),
            RetryBaseSeconds: ConfigurationReader.ReadInt(configuration, "Media:RetryBaseSeconds", d.RetryBaseSeconds, 1, 3600),
            LeaseMinutes: ConfigurationReader.ReadInt(configuration, "Media:LeaseMinutes", d.LeaseMinutes, 1, 120),
            RetentionIntervalMinutes: ConfigurationReader.ReadInt(configuration, "Media:RetentionIntervalMinutes", d.RetentionIntervalMinutes, 1, 1440),
            RetentionBatchSize: ConfigurationReader.ReadInt(configuration, "Media:RetentionBatchSize", d.RetentionBatchSize, 1, 10_000),
            DownloadTimeoutSeconds: ConfigurationReader.ReadInt(configuration, "Media:DownloadTimeoutSeconds", d.DownloadTimeoutSeconds, 5, 1800),
            OrphanSweepIntervalHours: ConfigurationReader.ReadInt(configuration, "Media:OrphanSweepIntervalHours", d.OrphanSweepIntervalHours, 1, 720),
            OrphanGraceHours: ConfigurationReader.ReadInt(configuration, "Media:OrphanGraceHours", d.OrphanGraceHours, 1, 720));
    }

    public static MediaStoreSettings GetValidatedStoreSettings(IConfiguration configuration)
    {
        var kind = GetValidatedOptions(configuration).Store;
        var container = configuration[AzureContainerKey] is { Length: > 0 } c ? c : "conversia-media";
        switch (kind)
        {
            case MediaStoreKind.Local:
                var root = configuration[LocalRootPathKey];
                if (string.IsNullOrWhiteSpace(root))
                {
                    throw new InvalidOperationException($"{LocalRootPathKey} is required when {StoreKey} is Local.");
                }

                byte[] key;
                try
                {
                    key = Convert.FromBase64String(configuration[LocalEncryptionKeyKey] ?? string.Empty);
                }
                catch (FormatException)
                {
                    throw new InvalidOperationException($"{LocalEncryptionKeyKey} must be base64 of 32 random bytes.");
                }

                if (key.Length != 32 || key.All(b => b == key[0]))
                {
                    throw new InvalidOperationException($"{LocalEncryptionKeyKey} must be base64 of 32 random bytes.");
                }

                return new MediaStoreSettings(kind, root, key, null, null, null, container);
            case MediaStoreKind.Azure:
                if (container.Length is < 3 or > 63 || !container.All(ch => char.IsAsciiLetterLower(ch) || char.IsAsciiDigit(ch) || ch == '-')
                    || container.StartsWith('-') || container.EndsWith('-') || container.Contains("--", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"{AzureContainerKey} is not a valid container name (3 to 63 lower-case letters, digits and single hyphens).");
                }

                var connection = configuration[AzureConnectionStringKey];
                var serviceUri = configuration[AzureServiceUriKey];
                if (string.IsNullOrWhiteSpace(connection) == string.IsNullOrWhiteSpace(serviceUri))
                {
                    throw new InvalidOperationException($"Exactly one of {AzureConnectionStringKey} or {AzureServiceUriKey} (managed identity) is required when {StoreKey} is Azure.");
                }

                Uri? uri = null;
                if (!string.IsNullOrWhiteSpace(serviceUri) && (!Uri.TryCreate(serviceUri, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps))
                {
                    throw new InvalidOperationException($"{AzureServiceUriKey} must be an absolute https URL.");
                }

                return new MediaStoreSettings(kind, null, null, string.IsNullOrWhiteSpace(connection) ? null : connection, uri,
                    configuration[AzureManagedIdentityClientIdKey] is { Length: > 0 } id ? id : null, container);
            default:
                return new MediaStoreSettings(kind, null, null, null, null, null, container);
        }
    }
}
