namespace Conversia.WebhookSimulator.Cli;

/// <summary>
/// The simulator sends fabricated conversations and logs in with real credentials: it must not be pointed at somebody's production by a slip.
/// Only a local address is accepted unless the person says <c>--allow-remote</c> on purpose, and even then a remote address must be <c>https</c>.
/// </summary>
public static class ApiUrlGuard
{
    public static Uri Check(string? url, bool allowRemote)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            throw new UsageException("--api-url must be an absolute http(s) address, for example http://localhost:5030.");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new UsageException("--api-url must not carry a user name or a password: credentials go in SIM_EMAIL and SIM_PASSWORD.");
        }

        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new UsageException("--api-url must be only the address of the API, without a query or a fragment.");
        }

        if (!allowRemote && !IsLocal(uri))
        {
            throw new UsageException(
                $"Refusing to talk to '{uri.Host}': the simulator sends fabricated conversations and is meant for a local API. " +
                "If you really want a remote one (a staging environment of yours), add --allow-remote.");
        }

        // The login, the token and the webhook secrets travel in this address: off the machine they must travel encrypted, --allow-remote or not.
        if (!IsLocal(uri) && uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new UsageException(
                $"Refusing plain http to '{uri.Host}': the login, the token and the webhook secrets would travel in cleartext. " +
                "A remote API must be reached over https; plain http is only accepted for localhost.");
        }

        return new Uri(uri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/");
    }

    public static bool IsLocal(Uri uri)
    {
        var host = uri.Host.Trim('[', ']');
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
               || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
               || (System.Net.IPAddress.TryParse(host, out var address) && System.Net.IPAddress.IsLoopback(address));
    }
}
