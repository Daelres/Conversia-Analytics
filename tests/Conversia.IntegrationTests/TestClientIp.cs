using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Conversia.IntegrationTests;

/// <summary>
/// The in-process TestServer gives every request a null remote address. Tests
/// that need distinct "client IPs" send <see cref="HeaderName"/>; the
/// <see cref="Filter"/> (registered by the test factories only) copies it into
/// <c>Connection.RemoteIpAddress</c> ahead of the whole API pipeline, exactly
/// where a reverse-proxy/forwarded-headers step would put the real address.
/// </summary>
internal static class TestClientIp
{
    public const string HeaderName = "X-Test-Client-Ip";

    private static int _next;

    /// <summary>A never-before-used address, so each test owns a fresh rate-limit partition.</summary>
    public static string Next()
    {
        var n = Interlocked.Increment(ref _next);
        return $"10.{(n >> 16) & 255}.{(n >> 8) & 255}.{n & 255}";
    }

    internal sealed class Filter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                if (context.Request.Headers.TryGetValue(HeaderName, out var value)
                    && IPAddress.TryParse(value.ToString(), out var address))
                {
                    context.Connection.RemoteIpAddress = address;
                }

                return nextMiddleware(context);
            });
            next(app);
        };
    }
}
