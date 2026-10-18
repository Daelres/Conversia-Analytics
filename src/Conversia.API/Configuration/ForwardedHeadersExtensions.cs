using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace Conversia.API.Configuration;

/// <summary>
/// Optional <c>X-Forwarded-For</c> support, <b>off unless proxies are named</b>. Behind a reverse proxy or tunnel every request otherwise
/// arrives from the proxy's address, so all callers would share one IP rate-limit budget. Configure the proxies explicitly:
/// <c>ForwardedHeaders:KnownProxies</c> (addresses) and/or <c>ForwardedHeaders:KnownNetworks</c> (CIDR ranges), as indexed lists
/// (<c>ForwardedHeaders__KnownProxies__0=10.0.0.5</c>). The header is honored only when the immediate sender is one of them, once
/// (<c>ForwardLimit = 1</c>); a header from anyone else, or with nothing configured, is ignored. Invalid entries stop the API at startup.
/// </summary>
public static class ForwardedHeadersExtensions
{
    public const string KnownProxiesKey = "ForwardedHeaders:KnownProxies";
    public const string KnownNetworksKey = "ForwardedHeaders:KnownNetworks";

    private sealed class Enabled;

    public static IServiceCollection AddOptionalForwardedHeaders(this IServiceCollection services, IConfiguration configuration)
    {
        var proxies = Entries(configuration, KnownProxiesKey)
            .Select(text => IPAddress.TryParse(text, out var address) ? address : throw new InvalidOperationException($"{KnownProxiesKey} holds an entry that is not an IP address."))
            .ToList();
        var networks = Entries(configuration, KnownNetworksKey)
            .Select(text => System.Net.IPNetwork.TryParse(text, out var network) ? network : throw new InvalidOperationException($"{KnownNetworksKey} holds an entry that is not a CIDR range (for example 10.0.0.0/8)."))
            .ToList();

        if (proxies.Count == 0 && networks.Count == 0)
        {
            return services;
        }

        services.AddSingleton<Enabled>();
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
            options.ForwardLimit = 1;
            options.KnownProxies.Clear(); // the defaults trust loopback; only what is configured is trusted
            options.KnownIPNetworks.Clear();
            foreach (var proxy in proxies)
            {
                options.KnownProxies.Add(proxy);
            }

            foreach (var network in networks)
            {
                options.KnownIPNetworks.Add(network);
            }
        });
        return services;
    }

    public static WebApplication UseOptionalForwardedHeaders(this WebApplication app)
    {
        if (app.Services.GetService<Enabled>() is not null)
        {
            app.UseForwardedHeaders();
        }

        return app;
    }

    private static IEnumerable<string> Entries(IConfiguration configuration, string key)
        => configuration.GetSection(key).GetChildren().Select(c => c.Value).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim());
}
