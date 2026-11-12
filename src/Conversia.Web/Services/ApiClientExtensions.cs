using Microsoft.Extensions.Options;

namespace Conversia.Web.Services;

public static class ApiClientExtensions
{
    public static IServiceCollection AddApiClient(this IServiceCollection services)
    {
        services.AddOptions<ApiOptions>()
            .BindConfiguration(ApiOptions.SectionName)
            .Validate(ApiOptions.IsValid, "Api:BaseUrl must be an absolute http(s) URL, Api:TimeoutSeconds between 1 and 300 and Api:SendTimeoutSeconds between 1 and 600.")
            .ValidateOnStart();

        // The handler pipeline's own logging is removed so request headers (the bearer token) can never reach a log.
        services.AddHttpClient<IApiClient, ApiClient>((provider, client) =>
            {
                var options = provider.GetRequiredService<IOptions<ApiOptions>>().Value;
                client.BaseAddress = options.BaseAddress;
                // The client enforces the limit of each call itself (a send waits longer than a read); this one is only the backstop behind both.
                client.Timeout = TimeSpan.FromSeconds(Math.Max(options.TimeoutSeconds, options.SendTimeoutSeconds) + 5);
            })
            .RemoveAllLoggers();

        return services;
    }
}
