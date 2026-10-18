namespace Conversia.API.RateLimiting;

internal static class ClientPartition
{
    private const string UnknownClient = "unknown-client";

    // Requests without a resolvable address share one budget rather than bypassing the limit.
    public static string Of(HttpContext httpContext) => httpContext.Connection.RemoteIpAddress?.ToString() ?? UnknownClient;
}
