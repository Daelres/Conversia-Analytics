using Conversia.Application.ErrorLogging;
using Conversia.Web.Services;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Conversia.Web.ErrorReporting;

public static class ErrorReportingServiceCollectionExtensions
{
    private const int MaxAnswerBytes = 64 * 1024;

    /// <summary>
    /// Registers the validated <c>ErrorReporting</c> options, the reporter with its HTTP client and the exception handler that uses it.
    /// An invalid section is refused here, while the Web is composed and before any host is built, like the other settings of the Web.
    /// </summary>
    /// <remarks>
    /// Refusing only at host start (<c>ValidateOnStart</c>) is not enough: <c>app.Run()</c> disposes the host as soon as its start fails,
    /// so whoever is still reaching into that host's services (a test host waiting for the start, for instance) gets an
    /// <see cref="ObjectDisposedException"/> instead of the refusal. <c>ValidateOnStart</c> stays as the backstop for configuration
    /// added after composition.
    /// </remarks>
    public static IServiceCollection AddWebErrorReporting(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        RefuseInvalid(configuration);

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

    // Reads and validates the section exactly as the options pipeline will; never puts the key in a message.
    private static void RefuseInvalid(IConfiguration configuration)
    {
        var options = new ErrorReportingOptions();
        new ErrorReportingOptionsSetup(configuration).Configure(options);
        var result = new ErrorReportingOptionsValidator().Validate(Options.DefaultName, options);
        if (result.Failed)
        {
            throw new OptionsValidationException(Options.DefaultName, typeof(ErrorReportingOptions), result.Failures);
        }
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
