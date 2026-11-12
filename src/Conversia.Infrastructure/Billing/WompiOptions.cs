using System.Security.Cryptography;
using System.Text;

namespace Conversia.Infrastructure.Billing;

public enum WompiEnvironment
{
    Sandbox,
    Production,
}

/// <summary>
/// <c>Billing:Wompi:*</c> (docs/BILLING.md, "Configuration"). The base URL follows the environment and is never read from configuration or
/// input: <c>https://sandbox.wompi.co/v1</c> or <c>https://production.wompi.co/v1</c> (https://docs.wompi.co/docs/colombia/ambientes-y-llaves/).
/// Each key must carry the prefix of its environment, so a production key can never run against the sandbox or the reverse. The private key,
/// the events secret and the integrity secret never leave the server and never reach a log.
/// </summary>
public sealed class WompiOptions
{
    public const string SectionName = "Billing:Wompi";

    public const string SandboxBaseUrl = "https://sandbox.wompi.co/v1";
    public const string ProductionBaseUrl = "https://production.wompi.co/v1";

    /// <summary>The checkout page of a payment link (https://docs.wompi.co/docs/colombia/links-de-pago/).</summary>
    public const string CheckoutLinkBaseUrl = "https://checkout.wompi.co/l/";

    public required WompiEnvironment Environment { get; init; }

    public required string PublicKey { get; init; }

    public required string PrivateKey { get; init; }

    public required string EventsSecret { get; init; }

    public required string IntegritySecret { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);

    public string BaseUrl => Environment == WompiEnvironment.Production ? ProductionBaseUrl : SandboxBaseUrl;

    /// <summary>What the events of this environment say in their <c>environment</c> field.</summary>
    public string EventEnvironment => Environment == WompiEnvironment.Production ? "prod" : "test";

    /// <summary>The prefixes of the keys of an environment, in the order public, private, events, integrity.</summary>
    public static (string Public, string Private, string Events, string Integrity) PrefixesOf(WompiEnvironment environment)
        => environment == WompiEnvironment.Production
            ? ("pub_prod_", "prv_prod_", "prod_events_", "prod_integrity_")
            : ("pub_test_", "prv_test_", "test_events_", "test_integrity_");
}

/// <summary>The two signatures of the gateway. Pure, so they are checked against the documented vectors.</summary>
public static class WompiSignatures
{
    /// <summary>
    /// The integrity signature of a transaction: SHA-256 (lowercase hex) of reference + amount in cents + currency + integrity secret, with no
    /// separators (https://docs.wompi.co/docs/colombia/widget-checkout-web/, "Firma de integridad").
    /// </summary>
    public static string Integrity(string reference, long amountInCents, string currency, string integritySecret)
        => Sha256Hex(reference + amountInCents.ToString(System.Globalization.CultureInfo.InvariantCulture) + currency + integritySecret);

    /// <summary>
    /// The checksum of an event: SHA-256 (hex) of the values of <c>signature.properties</c> in order, then the <c>timestamp</c>, then the events
    /// secret (https://docs.wompi.co/docs/colombia/eventos/).
    /// </summary>
    public static string EventChecksum(IEnumerable<string> propertyValues, string timestamp, string eventsSecret)
        => Sha256Hex(string.Concat(propertyValues) + timestamp + eventsSecret);

    /// <summary>Constant-time comparison of two hex digests, ignoring case.</summary>
    public static bool HexEquals(string expected, string? actual)
        => actual is not null
           && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected.ToLowerInvariant()), Encoding.ASCII.GetBytes(actual.Trim().ToLowerInvariant()));

    internal static string Sha256Hex(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
