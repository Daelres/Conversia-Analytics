namespace Conversia.Application.Licensing;

/// <summary>
/// <c>Licensing:RequireAssignedLicense</c> (default false): when true, a company can switch a line on only if a licence assigned to it matches
/// that number, and releasing a licence switches its line off. When false, lines work as before and licences are only linked for reporting.
/// See docs/PLATFORM_ADMIN.md, "Line licences".
/// </summary>
public sealed record LicensingSettings(bool RequireAssignedLicense)
{
    public const string RequireAssignedLicenseKey = "Licensing:RequireAssignedLicense";

    public static LicensingSettings Default { get; } = new(false);
}
