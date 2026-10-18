using Conversia.Application.Demo;

namespace Conversia.Infrastructure.Configuration;

/// <summary>Validated settings of the opt-in demo data. Present in the container only when the demo is enabled.</summary>
/// <remarks>A class, not a record: a record's generated <c>ToString()</c> would print the password.</remarks>
public sealed class DemoDataOptions(string password)
{
    public const string DefaultPassword = DemoAccounts.DefaultPassword;

    public string Password { get; } = password;

    public override string ToString() => nameof(DemoDataOptions);
}
