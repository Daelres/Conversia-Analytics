using Conversia.Application.ErrorLogging;
using Conversia.Web.Services;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Conversia.Web.ErrorReporting;

public static class ErrorReportingServiceCollectionExtensions
{
    private const int MaxAnswerBytes = 64 * 1024;

    /// <summary>Registers the validated <c>ErrorReporting</c> options (fail-fast at startup), the reporter with its HTTP client and the exception handler that uses it.</summary>
    public static IServiceCollection AddWebErrorReporting(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpContextAccessor();

        services.AddOptions<ErrorReportingOptions>().ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigureOptions<ErrorReportingOptions>, ErrorReportingOptionsSetup>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<ErrorReportingOptions>, ErrorReportingOptionsValidator>());

        services.AddSingleton(provider => new ErrorStormGuard(
            provider.GetRequiredService<IOptions<ErrorReportingOptions>>().Value.MaxPerFingerprintPerMinute,
            provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IWebErrorReporter, WebErrorReporter>();
        services.AddScoped<ErrorCodeState>();
        services.AddExceptionHandler<WebExceptionHandler>();
        AddReportClient(services);

        return services;
    }

    // Its own timeout is the options' (the reporter cancels the request itself); the handler pipeline's logging is removed
    // so the key header can never reach a log.
    // Redirects are off: a report never needs one, and the key header would follow it to another host.
    private static void AddReportClient(IServiceCollection services)
        => services.AddHttpClient(WebErrorReporter.HttpClientName, (provider, client) =>
            {
                client.BaseAddress = provider.GetRequiredService<IOptions<ApiOptions>>().Value.BaseAddress;
                client.Timeout = Timeout.InfiniteTimeSpan;
                client.MaxResponseContentBufferSize = MaxAnswerBytes;
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
            .RemoveAllLoggers();
}
