namespace Conversia.Infrastructure.Configuration;

/// <summary>
/// Settings of the platform administrator (section <c>PlatformAdmin</c>). <see cref="Email"/> and <see cref="Password"/> only bootstrap the
/// first administrator at startup; they never change an existing one.
/// </summary>
public sealed class PlatformAdminOptions
{
    public const string SectionName = "PlatformAdmin";
    public const string EmailKey = "PlatformAdmin:Email";
    public const string PasswordKey = "PlatformAdmin:Password";
    public const string PasswordFileKey = "PlatformAdmin:PasswordFile";
    public const string TokenMinutesKey = "PlatformAdmin:TokenMinutes";

    public const int DefaultTokenMinutes = 60;
    public const int MaxTokenMinutes = 720;
    public const int MinPasswordLength = 12;

    /// <summary>Email of the administrator to create when none with it exists; null or blank = no bootstrap.</summary>
    public string? Email { get; init; }

    private string? _password;

    /// <summary>
    /// Password for that bootstrap (from <c>PlatformAdmin:Password</c> or read from the file <c>PlatformAdmin:PasswordFile</c> names). Used once,
    /// to create the account; never stored or logged by this code, and forgotten by <see cref="ClearPassword"/> as soon as the bootstrap has run,
    /// so the long-lived options object does not keep it.
    /// </summary>
    public string? Password
    {
        get => _password;
        init => _password = value;
    }

    /// <summary>Forgets the password. The bootstrapper calls it once it is done with it.</summary>
    public void ClearPassword() => _password = null;

    /// <summary>How long an administrator's token lives. Short on purpose.</summary>
    public int TokenMinutes { get; init; } = DefaultTokenMinutes;

    public bool BootstrapRequested => !string.IsNullOrWhiteSpace(Email) && !string.IsNullOrEmpty(Password);
}
