using Conversia.Application.Demo;

namespace Conversia.Web.Demo;

/// <summary>Validated, opt-in settings of the demo pages. <see cref="Password"/> is meaningful only while <see cref="ShowAccounts"/> is on.</summary>
/// <remarks>A class, not a record: a record's generated <c>ToString()</c> would print the password.</remarks>
public sealed class DemoOptions(bool showAccounts, string password)
{
    public static DemoOptions Hidden { get; } = new(showAccounts: false, DemoAccounts.DefaultPassword);

    public bool ShowAccounts { get; } = showAccounts;

    public string Password { get; } = password;

    public override string ToString() => nameof(DemoOptions);
}
