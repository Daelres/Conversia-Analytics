using System.Net.Mail;
using Conversia.Infrastructure.Configuration;

namespace Conversia.API.Configuration;

/// <summary>
/// Fails fast at startup when the platform administrator settings are unsafe. The messages name the key, never the value. Nothing set
/// (the default) means no bootstrap: administrators are then created some other way, or none exists and the admin API is unreachable.
/// </summary>
public static class PlatformAdminOptionsValidator
{
    // Values that are what a template ships, not a decision of the operator (compared ignoring case and punctuation).
    private static readonly string[] PlaceholderMarkers = ["changeme", "replacewith", "replaceme", "placeholder", "yourpassword", "todo"];

    public static PlatformAdminOptions GetValidatedOptions(IConfiguration configuration)
    {
        var email = configuration[PlatformAdminOptions.EmailKey];
        var password = ReadPassword(configuration);
        var tokenMinutes = ConfigurationReader.ReadInt(
            configuration, PlatformAdminOptions.TokenMinutesKey, PlatformAdminOptions.DefaultTokenMinutes, 1, PlatformAdminOptions.MaxTokenMinutes);

        var hasEmail = !string.IsNullOrWhiteSpace(email);
        var hasPassword = !string.IsNullOrEmpty(password);
        if (hasEmail != hasPassword)
        {
            throw new InvalidOperationException(
                $"{(hasEmail ? PlatformAdminOptions.PasswordKey : PlatformAdminOptions.EmailKey)} is missing: {PlatformAdminOptions.EmailKey} and {PlatformAdminOptions.PasswordKey} must be set together (or both left empty).");
        }

        if (hasEmail)
        {
            if (!IsEmail(email!.Trim()))
            {
                throw new InvalidOperationException($"{PlatformAdminOptions.EmailKey} is not a valid email address.");
            }

            if (IsWeak(password!))
            {
                throw new InvalidOperationException(
                    $"{PlatformAdminOptions.PasswordKey} is too weak or still a placeholder: use at least {PlatformAdminOptions.MinPasswordLength} characters and not a placeholder taken from a template.");
            }
        }

        return new PlatformAdminOptions { Email = hasEmail ? email!.Trim() : null, Password = hasEmail ? password : null, TokenMinutes = tokenMinutes };
    }

    /// <summary>
    /// The password from <c>PlatformAdmin:Password</c>, or from the file <c>PlatformAdmin:PasswordFile</c> names (a Docker or Kubernetes secret):
    /// the trailing newline of the file is dropped. Giving both is a mistake, and a file that cannot be read or is empty stops the start. The errors
    /// name the key, never the path or the value.
    /// </summary>
    private static string? ReadPassword(IConfiguration configuration)
    {
        var password = configuration[PlatformAdminOptions.PasswordKey];
        var file = configuration[PlatformAdminOptions.PasswordFileKey];
        if (string.IsNullOrWhiteSpace(file))
        {
            return password;
        }

        if (!string.IsNullOrEmpty(password))
        {
            throw new InvalidOperationException($"{PlatformAdminOptions.PasswordKey} and {PlatformAdminOptions.PasswordFileKey} are both set: use only one of them.");
        }

        string content;
        try
        {
            content = File.ReadAllText(file.Trim());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new InvalidOperationException($"{PlatformAdminOptions.PasswordFileKey} points to a file that cannot be read.");
        }

        var fromFile = content.TrimEnd('\r', '\n');
        if (fromFile.Length == 0)
        {
            throw new InvalidOperationException($"{PlatformAdminOptions.PasswordFileKey} points to an empty file.");
        }

        return fromFile;
    }

    public static bool IsWeak(string password)
    {
        if (password.Length < PlatformAdminOptions.MinPasswordLength || password.Length > 128)
        {
            return true;
        }

        var letters = new string(password.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return PlaceholderMarkers.Any(letters.Contains) || password.Distinct().Count() < 4;
    }

    private static bool IsEmail(string email)
    {
        try
        {
            var address = new MailAddress(email);
            return address.Address == email && email.Length <= 256 && !email.Contains('\0');
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
