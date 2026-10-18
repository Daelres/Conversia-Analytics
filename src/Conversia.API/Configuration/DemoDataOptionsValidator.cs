using Conversia.Application.Validation;
using Conversia.Infrastructure.Configuration;

namespace Conversia.API.Configuration;

/// <summary>
/// Fails fast at startup when the opt-in demo data is misconfigured: a <c>Demo:Enabled</c> that is not a boolean or is
/// on outside the Development/Demo environments, or a <c>Demo:Password</c> that breaks the registration password
/// policy. Error messages never contain the password.
/// </summary>
internal static class DemoDataOptionsValidator
{
    public const string EnabledKey = "Demo:Enabled";
    public const string PasswordKey = "Demo:Password";
    public const string DemoEnvironmentName = "Demo";

    /// <summary>The validated options, or <c>null</c> when the demo data is not enabled.</summary>
    public static DemoDataOptions? GetValidatedOptions(IConfiguration configuration, IHostEnvironment environment)
    {
        if (!IsEnabled(configuration))
        {
            return null;
        }

        if (!environment.IsDevelopment() && !environment.IsEnvironment(DemoEnvironmentName))
        {
            throw new InvalidOperationException(
                $"{EnabledKey} may only be true in the Development or {DemoEnvironmentName} environment, " +
                $"not '{environment.EnvironmentName}'. Demo accounts have publicly known credentials.");
        }

        return new DemoDataOptions(ReadPassword(configuration));
    }

    private static bool IsEnabled(IConfiguration configuration)
    {
        var raw = configuration[EnabledKey];

        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        if (!bool.TryParse(raw, out var enabled))
        {
            throw new InvalidOperationException($"{EnabledKey} must be 'true' or 'false'.");
        }

        return enabled;
    }

    private static string ReadPassword(IConfiguration configuration)
    {
        var password = configuration[PasswordKey];

        if (string.IsNullOrEmpty(password))
        {
            return DemoDataOptions.DefaultPassword;
        }

        if (!new StrongPasswordAttribute().IsValid(password))
        {
            throw new InvalidOperationException(
                $"{PasswordKey} must be {StrongPasswordAttribute.MinLength}-{StrongPasswordAttribute.MaxLength} characters long " +
                "and contain at least one uppercase letter, one lowercase letter and one digit.");
        }

        return password;
    }
}
