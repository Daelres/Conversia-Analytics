using Conversia.Application.Demo;
using Conversia.Application.Validation;

namespace Conversia.Web.Demo;

public static class DemoServiceCollectionExtensions
{
    public const string ShowAccountsKey = "Demo:ShowAccounts";
    public const string PasswordKey = "Demo:Password";
    private const string DemoEnvironmentName = "Demo";

    /// <summary>
    /// Registers the demo settings, failing fast at startup when <c>Demo:ShowAccounts</c> is not a boolean or is on outside
    /// the Development/Demo environments (the accounts have publicly known credentials), or when a shown <c>Demo:Password</c>
    /// breaks the registration password policy. Error messages never contain the password.
    /// </summary>
    public static IServiceCollection AddDemoAccounts(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddSingleton(Read(configuration, environment));
        return services;
    }

    private static DemoOptions Read(IConfiguration configuration, IHostEnvironment environment)
    {
        if (!IsShowingAccounts(configuration))
        {
            return DemoOptions.Hidden;
        }

        if (!environment.IsDevelopment() && !environment.IsEnvironment(DemoEnvironmentName))
        {
            throw new InvalidOperationException(
                $"{ShowAccountsKey} may only be true in the Development or {DemoEnvironmentName} environment, " +
                $"not '{environment.EnvironmentName}'. Demo accounts have publicly known credentials.");
        }

        return new DemoOptions(showAccounts: true, ReadPassword(configuration));
    }

    private static bool IsShowingAccounts(IConfiguration configuration)
    {
        var raw = configuration[ShowAccountsKey];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        return bool.TryParse(raw, out var show)
            ? show
            : throw new InvalidOperationException($"{ShowAccountsKey} must be 'true' or 'false'.");
    }

    private static string ReadPassword(IConfiguration configuration)
    {
        var password = configuration[PasswordKey];
        if (string.IsNullOrEmpty(password))
        {
            return DemoAccounts.DefaultPassword;
        }

        return new StrongPasswordAttribute().IsValid(password)
            ? password
            : throw new InvalidOperationException(
                $"{PasswordKey} must be {StrongPasswordAttribute.MinLength}-{StrongPasswordAttribute.MaxLength} characters long " +
                "and contain at least one uppercase letter, one lowercase letter and one digit.");
    }
}
