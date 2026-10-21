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
    public const string TokenMinutesKey = "PlatformAdmin:TokenMinutes";

    public const int DefaultTokenMinutes = 60;
    public const int MaxTokenMinutes = 720;
    public const int MinPasswordLength = 12;

    /// <summary>Email of the administrator to create when none with it exists; null or blank = no bootstrap.</summary>
    public string? Email { get; init; }

    /// <summary>Password for that bootstrap. Used once, to create the account; never stored or logged by this code.</summary>
    public string? Password { get; init; }

    /// <summary>How long an administrator's token lives. Short on purpose.</summary>
    public int TokenMinutes { get; init; } = DefaultTokenMinutes;

    public bool BootstrapRequested => !string.IsNullOrWhiteSpace(Email) && !string.IsNullOrEmpty(Password);
}
