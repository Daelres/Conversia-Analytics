using Conversia.Domain.Enums;

namespace Conversia.Web.IntegrationTests.Infrastructure;

/// <summary>One account of the API's demo data, spelled out independently of the Web (docs/USERS_TENANTS_PERMISSIONS.md, "Demo data").</summary>
/// <param name="Key">Short name used in test data.</param>
/// <param name="ListedSku">A SKU of that account's own tenant.</param>
/// <param name="ForeignSku">A SKU only the other demo tenant has.</param>
internal sealed record DemoAccount(
    string Key, string Email, bool IsOwner, Permission[] Permissions, string ListedSku, string ForeignSku);

/// <summary>
/// The demo tenants are seeded once into the shared database by <see cref="TestApiFactory"/> and are strictly read-only for
/// tests; anything that changes data uses a tenant of its own. Having the list here, separate from the Web, is what makes the
/// "every displayed account really logs in" tests a drift guard: the Web's list and the API seeder are compared against it.
/// </summary>
internal static class DemoAccounts
{
    public const string Password = "Demo-Pass-2026!";
    public const string SharedSku = "SKU-001";

    public const string AcmeOwnerEmail = "owner@acme-demo.example";
    public const string AcmeManagerEmail = "manager@acme-demo.example";
    public const string AcmeViewerEmail = "viewer@acme-demo.example";
    public const string AcmeNoAccessEmail = "noaccess@acme-demo.example";
    public const string GlobexOwnerEmail = "owner@globex-demo.example";

    public static readonly string[] AcmeSkus = Enumerable.Range(1, 12).Select(i => $"ACME-{i:000}").Append(SharedSku).ToArray();

    public static readonly string[] GlobexSkus = { SharedSku, "GLX-002", "GLX-003", "GLX-004", "GLX-005" };

    public static readonly IReadOnlyList<DemoAccount> All = new[]
    {
        new DemoAccount("owner", AcmeOwnerEmail, true, Array.Empty<Permission>(), "ACME-001", "GLX-002"),
        new DemoAccount("manager", AcmeManagerEmail, false, new[] { Permission.ProductsView, Permission.ProductsManage, Permission.UsersView }, "ACME-001", "GLX-002"),
        new DemoAccount("viewer", AcmeViewerEmail, false, new[] { Permission.ProductsView }, "ACME-001", "GLX-002"),
        new DemoAccount("noaccess", AcmeNoAccessEmail, false, Array.Empty<Permission>(), "ACME-001", "GLX-002"),
        new DemoAccount("globex-owner", GlobexOwnerEmail, true, Array.Empty<Permission>(), "GLX-002", "ACME-001"),
    };

    public static readonly string[] AllEmails = All.Select(a => a.Email).ToArray();

    public static DemoAccount Get(string key) => All.Single(a => a.Key == key);

    public static TheoryData<string> Keys
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var account in All)
            {
                data.Add(account.Key);
            }

            return data;
        }
    }

    /// <summary>Markers of the demo accounts that must not appear anywhere while <c>Demo:ShowAccounts</c> is off.</summary>
    public static readonly string[] Secrets = { "acme-demo.example", "globex-demo.example", Password };
}
