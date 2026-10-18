using Microsoft.Extensions.Options;

namespace Conversia.Web.Services;

public static class ApiClientExtensions
{
    public static IServiceCollection AddApiClient(this IServiceCollection services)
    {
        services.AddOptions<ApiOptions>()
            .BindConfiguration(ApiOptions.SectionName)
            .Validate(ApiOptions.IsValid, "Api:BaseUrl must be an absolute http(s) URL and Api:TimeoutSeconds between 1 and 300.")
            .ValidateOnStart();

        // The handler pipeline's own logging is removed so request headers (the bearer token) can never reach a log.
        services.AddHttpClient<IApiClient, ApiClient>((provider, client) =>
            {
                var options = provider.GetRequiredService<IOptions<ApiOptions>>().Value;
                client.BaseAddress = options.BaseAddress;
                client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
            })
            .RemoveAllLoggers();

        return services;
    }
}
