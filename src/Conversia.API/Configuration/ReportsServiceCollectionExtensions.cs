using System.Globalization;
using System.Text;
using Conversia.API.RateLimiting;
using Conversia.Application.Classification.Llm;
using Conversia.Application.Reports;
using Conversia.Application.Time;
using Conversia.Infrastructure.Configuration;
using Conversia.Infrastructure.Email;
using Conversia.Infrastructure.Persistence;
using Conversia.Infrastructure.Reports;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MimeKit;

namespace Conversia.API.Configuration;

/// <summary>
/// The manager reports (docs/REPORTS.md): settings, the email adapter, the writer (model or template), the services, the job and the rate limits
/// of the preview and of the unsubscribe link. Invalid values stop the API at startup naming the key, never printing a value (the SMTP password
/// and the From address included).
/// </summary>
public static class ReportsServiceCollectionExtensions
{
    public const string JobEnabledKey = "Reports:Job:Enabled";
    public const string JobIntervalKey = "Reports:Job:IntervalSeconds";
    public const string MaxDefinitionsKey = "Reports:MaxDefinitionsPerCompany";
    public const string MaxRecipientsKey = "Reports:MaxRecipientsPerDefinition";
    public const string RetentionDaysKey = "Reports:RetentionDays";
    public const string CatchUpHoursKey = "Reports:CatchUpHours";
    public const string MaxDeliveryAttemptsKey = "Reports:MaxDeliveryAttempts";
    public const string PreviewsPerHourKey = "Reports:PreviewsPerHour";
    public const string AiEnabledKey = "Reports:Ai:Enabled";
    public const string MonthlyLlmReportsKey = "Reports:Ai:MonthlyReportsPerCompany";
    public const string DeploymentUsdCapKey = "Reports:Ai:DeploymentMonthlyUsdCap";
    public const string WebBaseUrlKey = "Reports:WebBaseUrl";
    public const string MinConversionSampleKey = "Reports:MinConversionSample";
    public const string PreviewsPerCompanyPerDayKey = "Reports:PreviewsPerCompanyPerDay";
    public const string UnsubscribePerTokenKey = "Reports:Unsubscribe:PerLinkPerMinute";
    public const string UnsubscribeJunkKey = "Reports:Unsubscribe:InvalidPerMinute";

    public static IServiceCollection AddReports(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.TryAddSingleton(TimeProvider.System);
        var settings = GetValidatedSettings(configuration, environment);
        services.AddSingleton(settings);
        var email = GetValidatedEmailOptions(configuration, environment);
        services.AddSingleton(email);

        // The unsubscribe links are signed with a key derived from the JWT signing key (HKDF): no new secret to configure. Rotating the signing
        // key invalidates the links of the emails already sent (the landing says the link is not valid; the person can ask to be removed).
        // A dedicated key (Reports:UnsubscribeKey, base64 of at least 32 bytes) when set, so the JWT key can rotate without breaking the links.
        services.AddSingleton(new ReportUnsubscribeKey(GetUnsubscribeKey(configuration)));

        switch (email.Provider)
        {
            case EmailOptions.SmtpProvider:
                var password = configuration[EmailOptions.SmtpPasswordKey];
                configuration[EmailOptions.SmtpPasswordKey] = null; // the adapter holds it from here on
                services.AddSingleton<IEmailSender>(sp => new SmtpEmailSender(email, password, sp.GetRequiredService<ILogger<SmtpEmailSender>>()));
                break;
            case EmailOptions.PickupProvider:
                services.AddSingleton<IEmailSender>(new PickupDirectoryEmailSender(email));
                break;
            default:
                services.AddSingleton<IEmailSender, DisabledEmailSender>();
                break;
        }

        services.AddScoped<IReportStore, ReportStore>();
        services.AddScoped<ReportDataPackBuilder>();
        services.AddScoped<IReportAiAdmission>(sp => new ReportAiAdmission(
            sp.GetRequiredService<ApplicationDbContext>(), settings, sp.GetRequiredService<AiQuotaSettings>(), sp.GetRequiredService<TimeProvider>(),
            sp.GetService<LlmOptions>()?.SandboxOnly ?? false));
        // The model is the conversation analysis' own client (Classification:Provider=Llm, Development only until its gate is lifted): without it,
        // or with Reports:Ai:Enabled=false, every report is the template.
        services.AddScoped(sp => new ReportComposer(
            settings.AiEnabled ? sp.GetService<ILlmClient>() : null,
            sp.GetRequiredService<IReportAiAdmission>(),
            sp.GetService<LlmOptions>()?.Pricing ?? LlmPricing.Default,
            sp.GetRequiredService<ILogger<ReportComposer>>()));
        services.AddScoped(sp => new ReportWriterService(
            sp.GetRequiredService<IReportStore>(), sp.GetRequiredService<ReportDataPackBuilder>(), sp.GetRequiredService<ReportComposer>(),
            sp.GetRequiredService<TenantTimeZone>(), sp.GetRequiredService<TimeProvider>()));
        services.AddScoped<IReportWhatsAppCodeSender, ReportWhatsAppCodeSender>();
        services.AddScoped<IReportService, ReportService>();
        services.AddScoped<IReportMonitoringQueries>(sp => new ReportMonitoringQueries(
            sp.GetRequiredService<ApplicationDbContext>(), sp.GetRequiredService<AiQuotaSettings>(), email, sp.GetRequiredService<TimeProvider>(),
            settings.AiEnabled && sp.GetService<ILlmClient>() is not null));

        services.AddSingleton<ReportJobRunner>();
        if (settings.JobEnabled)
        {
            services.AddHostedService<ReportJobService>();
        }

        services.AddReportRateLimiting(settings);
        return services;
    }

    public const string UnsubscribeKeyKey = "Reports:UnsubscribeKey";

    public static byte[] GetUnsubscribeKey(IConfiguration configuration)
    {
        var raw = configuration[UnsubscribeKeyKey];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return ReportUnsubscribeToken.DeriveKey(Encoding.UTF8.GetBytes(JwtSigningKeyValidator.GetValidatedSigningKey(configuration)));
        }

        byte[] key;
        try
        {
            key = Convert.FromBase64String(raw.Trim());
        }
        catch (FormatException)
        {
            throw new InvalidOperationException($"{UnsubscribeKeyKey} must be base64 of at least 32 random bytes.");
        }

        configuration[UnsubscribeKeyKey] = null;
        return key.Length >= 32 ? key : throw new InvalidOperationException($"{UnsubscribeKeyKey} must be base64 of at least 32 random bytes.");
    }

    public static ReportSettings GetValidatedSettings(IConfiguration configuration, IHostEnvironment environment)
    {
        var defaults = ReportSettings.Default;
        return new ReportSettings(
            ConfigurationReader.ReadBool(configuration, JobEnabledKey, defaults.JobEnabled),
            ConfigurationReader.ReadInt(configuration, JobIntervalKey, defaults.JobIntervalSeconds, 5, 3600),
            ConfigurationReader.ReadInt(configuration, MaxDefinitionsKey, defaults.MaxDefinitionsPerCompany, 1, 100),
            ConfigurationReader.ReadInt(configuration, MaxRecipientsKey, defaults.MaxRecipientsPerDefinition, 1, Domain.Entities.ReportDefinition.MaxRecipients),
            ConfigurationReader.ReadInt(configuration, RetentionDaysKey, defaults.RetentionDays, 7, 3650),
            ConfigurationReader.ReadInt(configuration, CatchUpHoursKey, defaults.CatchUpHours, 1, 168),
            ConfigurationReader.ReadInt(configuration, MaxDeliveryAttemptsKey, defaults.MaxDeliveryAttempts, 1, 10),
            ConfigurationReader.ReadInt(configuration, PreviewsPerHourKey, defaults.PreviewsPerHour, 1, 1000),
            ConfigurationReader.ReadBool(configuration, AiEnabledKey, defaults.AiEnabled),
            ConfigurationReader.ReadInt(configuration, MonthlyLlmReportsKey, defaults.MonthlyLlmReportsPerCompany, 0, 10_000),
            ReadUsd(configuration, DeploymentUsdCapKey, defaults.DeploymentMonthlyUsdCap),
            ReadWebBaseUrl(configuration, environment, defaults.WebBaseUrl),
            ConfigurationReader.ReadInt(configuration, MinConversionSampleKey, defaults.MinConversionSample, 1, 100_000),
            ConfigurationReader.ReadInt(configuration, PreviewsPerCompanyPerDayKey, defaults.PreviewsPerCompanyPerDay, 0, 10_000),
            ConfigurationReader.ReadInt(configuration, UnsubscribePerTokenKey, defaults.UnsubscribePerTokenPerMinute, 1, 10_000),
            ConfigurationReader.ReadInt(configuration, UnsubscribeJunkKey, defaults.UnsubscribeJunkPerMinute, 1, 100_000));
    }

    /// <summary>
    /// The <c>Email</c> section: the provider, the sender identity (a valid address, a one-line name) and the SMTP server. TLS is not optional;
    /// the folder of <c>.eml</c> files is accepted only in Development.
    /// </summary>
    public static EmailOptions GetValidatedEmailOptions(IConfiguration configuration, IHostEnvironment environment)
    {
        var raw = configuration[EmailOptions.ProviderKey];
        var provider = string.IsNullOrWhiteSpace(raw) ? EmailOptions.NoneProvider : raw.Trim();
        provider = new[] { EmailOptions.NoneProvider, EmailOptions.SmtpProvider, EmailOptions.PickupProvider }
            .FirstOrDefault(p => string.Equals(p, provider, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"{EmailOptions.ProviderKey} must be '{EmailOptions.NoneProvider}', '{EmailOptions.SmtpProvider}' or '{EmailOptions.PickupProvider}'.");
        if (provider == EmailOptions.NoneProvider)
        {
            return EmailOptions.Disabled;
        }

        var from = configuration[EmailOptions.FromAddressKey]?.Trim();
        if (string.IsNullOrEmpty(from) || !MailboxAddress.TryParse(from, out var mailbox) || mailbox.Address != from || !from.Contains('@'))
        {
            throw new InvalidOperationException($"{EmailOptions.FromAddressKey} must be a valid email address (the sender of the reports) when {EmailOptions.ProviderKey} is '{provider}'.");
        }

        var name = configuration[EmailOptions.FromNameKey]?.Trim();
        if (name is { Length: > 100 } || (name is not null && name.Any(char.IsControl)))
        {
            throw new InvalidOperationException($"{EmailOptions.FromNameKey} must be one line of at most 100 characters.");
        }

        if (provider == EmailOptions.PickupProvider)
        {
            if (!environment.IsDevelopment())
            {
                throw new InvalidOperationException($"{EmailOptions.ProviderKey}='{EmailOptions.PickupProvider}' is accepted only in the Development environment.");
            }

            var directory = configuration[EmailOptions.PickupDirectoryKey]?.Trim();
            return new EmailOptions(provider, from, string.IsNullOrEmpty(name) ? null : name, null, string.IsNullOrEmpty(directory) ? Path.Combine(Path.GetTempPath(), "conversia-email") : directory);
        }

        var host = configuration[EmailOptions.SmtpHostKey]?.Trim();
        if (string.IsNullOrEmpty(host) || Uri.CheckHostName(host) == UriHostNameType.Unknown)
        {
            throw new InvalidOperationException($"{EmailOptions.SmtpHostKey} must be the host name of the SMTP server.");
        }

        var security = configuration[EmailOptions.SmtpSecurityKey]?.Trim();
        security = string.IsNullOrEmpty(security) ? SmtpOptions.StartTls : security;
        security = new[] { SmtpOptions.StartTls, SmtpOptions.SslOnConnect }.FirstOrDefault(s => string.Equals(s, security, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"{EmailOptions.SmtpSecurityKey} must be '{SmtpOptions.StartTls}' or '{SmtpOptions.SslOnConnect}': the reports are never sent without TLS.");
        var user = configuration[EmailOptions.SmtpUserNameKey]?.Trim();
        if (!string.IsNullOrEmpty(user) && string.IsNullOrEmpty(configuration[EmailOptions.SmtpPasswordKey]))
        {
            throw new InvalidOperationException($"{EmailOptions.SmtpPasswordKey} is required with {EmailOptions.SmtpUserNameKey} (set it from the environment, Email__Smtp__Password, never in a file of the repository).");
        }

        return new EmailOptions(provider, from, string.IsNullOrEmpty(name) ? null : name, new SmtpOptions(
            host,
            ConfigurationReader.ReadInt(configuration, EmailOptions.SmtpPortKey, security == SmtpOptions.SslOnConnect ? 465 : 587, 1, 65535),
            security,
            string.IsNullOrEmpty(user) ? null : user,
            ConfigurationReader.ReadInt(configuration, EmailOptions.SmtpTimeoutSecondsKey, 30, 1, 300)), null);
    }

    private static decimal ReadUsd(IConfiguration configuration, string key, decimal defaultValue)
    {
        var raw = configuration[key];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        return decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) && value is >= 0m and <= 100_000m
            ? value
            : throw new InvalidOperationException($"{key} must be a number of USD between 0 and 100000 (a dot as decimal mark).");
    }

    // The public address of the Web, for the links of the emails and notices: https, except for a local address while developing.
    private static Uri ReadWebBaseUrl(IConfiguration configuration, IHostEnvironment environment, Uri defaultValue)
    {
        var raw = configuration[WebBaseUrlKey]?.Trim();
        var sendsEmail = !string.IsNullOrWhiteSpace(configuration[EmailOptions.ProviderKey])
            && !string.Equals(configuration[EmailOptions.ProviderKey]!.Trim(), EmailOptions.NoneProvider, StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(raw))
        {
            // Emails carry the links: outside Development, a deployment that sends them must say where its Web is.
            return sendsEmail && !environment.IsDevelopment()
                ? throw new InvalidOperationException($"{WebBaseUrlKey} is required (an https address) when {EmailOptions.ProviderKey} sends emails outside Development.")
                : defaultValue;
        }

        if (!Uri.TryCreate(raw.EndsWith('/') ? raw : raw + "/", UriKind.Absolute, out var url) || url.Scheme is not ("https" or "http")
            || !string.IsNullOrEmpty(url.Query) || !string.IsNullOrEmpty(url.Fragment) || !string.IsNullOrEmpty(url.UserInfo))
        {
            throw new InvalidOperationException($"{WebBaseUrlKey} must be the absolute address of the Web (https://...), without query or fragment.");
        }

        if (url.Scheme == "http" && !environment.IsDevelopment() && (sendsEmail || !url.IsLoopback))
        {
            throw new InvalidOperationException($"{WebBaseUrlKey} must use https outside Development (the links of the emails carry the unsubscribe token).");
        }

        return url;
    }
}
