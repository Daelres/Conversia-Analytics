using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Conversia.IntegrationTests;

/// <summary>
/// Configuration of the login rate limit (<c>RateLimiting:Login</c>): fail-fast on
/// invalid values (same style as <see cref="JwtSigningKeyStartupTests"/>) and the
/// shipped defaults. Requests used here are malformed login bodies, which the
/// API answers with 400 without ever reaching the database, so none of these
/// tests need the SQL container.
/// </summary>
public class LoginRateLimitConfigurationTests
{
    private const string PermitLimitSetting = "RateLimiting:Login:PermitLimit";
    private const string WindowSecondsSetting = "RateLimiting:Login:WindowSeconds";

    private sealed class RateLimitFactory(
        IReadOnlyDictionary<string, string> settings, bool withoutAppSettingsFile = false)
        : WebApplicationFactory<Program>
    {
        private readonly string _emptyContentRoot = Path.Combine(Path.GetTempPath(), $"ratelimit-startup-{Guid.NewGuid():N}");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // appsettings.json ships a placeholder signing key the API refuses to boot with.
            builder.UseSetting("Jwt:SigningKey", TestJwtSettings.SigningKey);
            builder.UseSetting("Webhooks:Processing:Enabled", "false"); // no queue worker: these hosts assert on database traffic and logs
            builder.UseSetting("Webhooks:Retention:Enabled", "false");
            builder.UseSetting("Conversations:AutoCloseEnabled", "false");
            builder.UseSetting("Classification:Enabled", "false");

            if (withoutAppSettingsFile)
            {
                // An empty content root hides the API's appsettings.json, so the
                // RateLimiting section is genuinely absent.
                Directory.CreateDirectory(_emptyContentRoot);
                builder.UseContentRoot(_emptyContentRoot);
            }

            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureServices(services =>
                services.AddSingleton<IStartupFilter, TestClientIp.Filter>());
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && Directory.Exists(_emptyContentRoot))
            {
                Directory.Delete(_emptyContentRoot, recursive: true);
            }
        }
    }

    private static Dictionary<string, string> Settings(string permitLimit, string windowSeconds) => new()
    {
        [PermitLimitSetting] = permitLimit,
        [WindowSecondsSetting] = windowSeconds,
    };

    private static Task<HttpResponseMessage> SendMalformedLoginAsync(HttpClient client)
        => client.PostAsync("/api/auth/login", new StringContent("{not json", Encoding.UTF8, "application/json"));

    /// <summary>Sends login requests from a single fresh address until the first 429; returns how many were let through.</summary>
    private static async Task<int> CountRequestsAllowedBeforeThrottleAsync(WebApplicationFactory<Program> factory, int safetyCap)
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestClientIp.HeaderName, TestClientIp.Next());

        for (var allowed = 0; allowed < safetyCap; allowed++)
        {
            using var response = await SendMalformedLoginAsync(client);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return allowed;
            }

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        return safetyCap;
    }

    public static TheoryData<string, string, string> InvalidSettings => new()
    {
        { PermitLimitSetting, "0", "zero permits" },
        { PermitLimitSetting, "-1", "negative permits" },
        { PermitLimitSetting, "-2147483648", "int.MinValue permits" },
        { PermitLimitSetting, "abc", "non-numeric permits" },
        { PermitLimitSetting, "10 requests", "number with trailing text" },
        { WindowSecondsSetting, "0", "zero-second window" },
        { WindowSecondsSetting, "-60", "negative window" },
        { WindowSecondsSetting, "-2147483648", "int.MinValue window" },
        { WindowSecondsSetting, "sixty", "non-numeric window" },
        { WindowSecondsSetting, "0.5", "fractional window" },
        { WindowSecondsSetting, "60s", "number with a unit suffix" },
    };

    [Theory]
    [MemberData(nameof(InvalidSettings))]
    public void Boot_WithInvalidRateLimitSetting_FailsWithInvalidOperationExceptionNamingTheSetting(
        string setting, string value, string reason)
    {
        var settings = Settings(permitLimit: "10", windowSeconds: "60");
        settings[setting] = value;
        using var factory = new RateLimitFactory(settings);

        var failure = Record.Exception(() => factory.CreateClient());

        Assert.True(failure is not null, $"Host booted with an invalid {setting} ({reason}: '{value}').");
        var startupError = FindInChain<InvalidOperationException>(failure!, e => e.Message.Contains(setting));
        Assert.True(
            startupError is not null,
            $"Expected an InvalidOperationException naming '{setting}' ({reason}); got: {Describe(failure!)}");
    }

    [Fact]
    public void Boot_WithBothSettingsInvalid_FailsWithInvalidOperationExceptionNamingAtLeastOneOfThem()
    {
        using var factory = new RateLimitFactory(Settings(permitLimit: "0", windowSeconds: "0"));

        var failure = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(failure);
        var startupError = FindInChain<InvalidOperationException>(
            failure!, e => e.Message.Contains(PermitLimitSetting) || e.Message.Contains(WindowSecondsSetting));
        Assert.True(startupError is not null, $"Expected a named InvalidOperationException; got: {Describe(failure!)}");
    }

    public static TheoryData<string, string> AcceptableSettings => new()
    {
        { "1", "1" },
        { "10", "60" },
        { "1000", "3600" },
    };

    [Theory]
    [MemberData(nameof(AcceptableSettings))]
    public async Task Boot_WithValidRateLimitSettings_StartsAndServesRequests(string permitLimit, string windowSeconds)
    {
        using var factory = new RateLimitFactory(Settings(permitLimit, windowSeconds));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestClientIp.HeaderName, TestClientIp.Next());

        using var response = await SendMalformedLoginAsync(client);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("1", "60", 1)]
    [InlineData("4", "60", 4)]
    public async Task Login_UsesTheConfiguredPermitLimit(string permitLimit, string windowSeconds, int expectedAllowed)
    {
        using var factory = new RateLimitFactory(Settings(permitLimit, windowSeconds));

        var allowed = await CountRequestsAllowedBeforeThrottleAsync(factory, safetyCap: 50);

        Assert.Equal(expectedAllowed, allowed);
    }

    [Fact]
    public async Task Login_WithShippedAppSettings_AllowsTenRequestsThenThrottles()
    {
        // No RateLimiting override at all: the API runs with what appsettings.json ships.
        using var factory = new RateLimitFactory(new Dictionary<string, string>());

        var allowed = await CountRequestsAllowedBeforeThrottleAsync(factory, safetyCap: 50);

        Assert.Equal(10, allowed);
    }

    [Fact]
    public async Task Login_WithoutRateLimitingSection_FallsBackToTenRequestsPerWindow()
    {
        using var factory = new RateLimitFactory(new Dictionary<string, string>(), withoutAppSettingsFile: true);

        var allowed = await CountRequestsAllowedBeforeThrottleAsync(factory, safetyCap: 50);

        Assert.Equal(10, allowed);
    }

    [Fact]
    public async Task Login_WithOnlyPermitLimitConfigured_EitherFailsNamingWindowSecondsOrStillThrottlesWithRetryAfter()
    {
        // The requirement gives defaults only for an absent section, not for a single missing key, so both
        // a fail-fast boot naming WindowSeconds and a defaulted window are acceptable. What is not acceptable
        // is booting without any working window: after the permit is spent the next request must be 429.
        using var factory = new RateLimitFactory(
            new Dictionary<string, string> { [PermitLimitSetting] = "1" }, withoutAppSettingsFile: true);

        HttpResponseMessage first;
        HttpResponseMessage throttled;
        var client = default(HttpClient);
        try
        {
            client = factory.CreateClient();
            client.DefaultRequestHeaders.Add(TestClientIp.HeaderName, TestClientIp.Next());
            first = await SendMalformedLoginAsync(client);
            throttled = await SendMalformedLoginAsync(client);
        }
        catch (Exception failure)
        {
            client?.Dispose();
            var startupError = FindInChain<InvalidOperationException>(failure, e => e.Message.Contains(WindowSecondsSetting));
            Assert.True(
                startupError is not null,
                $"A missing '{WindowSecondsSetting}' may fail boot only with an InvalidOperationException naming it; got: {Describe(failure)}");
            return;
        }

        using (client)
        using (first)
        using (throttled)
        {
            Assert.Equal(HttpStatusCode.BadRequest, first.StatusCode);
            Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
            Assert.True(throttled.Headers.TryGetValues("Retry-After", out var values), "429 must carry Retry-After.");
            Assert.True(int.Parse(values!.Single()) >= 1, "Retry-After must be at least 1 second.");
        }
    }

    [Fact]
    public void ShippedAppSettings_DeclareTenRequestsPerSixtySeconds()
    {
        using var factory = new RateLimitFactory(new Dictionary<string, string>());
        var contentRoot = factory.Services.GetRequiredService<IWebHostEnvironment>().ContentRootPath;

        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(contentRoot, "appsettings.json")));

        Assert.True(
            document.RootElement.TryGetProperty("RateLimiting", out var rateLimiting),
            "appsettings.json must ship a RateLimiting section.");
        Assert.True(rateLimiting.TryGetProperty("Login", out var login), "appsettings.json must ship RateLimiting:Login.");
        Assert.Equal(10, login.GetProperty("PermitLimit").GetInt32());
        Assert.Equal(60, login.GetProperty("WindowSeconds").GetInt32());
    }

    private static T? FindInChain<T>(Exception root, Func<T, bool> predicate) where T : Exception
    {
        for (var e = root; e is not null; e = e.InnerException)
        {
            if (e is T match && predicate(match))
            {
                return match;
            }

            if (e is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    var found = FindInChain(inner, predicate);
                    if (found is not null)
                    {
                        return found;
                    }
                }
            }
        }

        return null;
    }

    private static string Describe(Exception root)
    {
        var parts = new List<string>();
        for (var e = root; e is not null; e = e.InnerException)
        {
            parts.Add($"{e.GetType().Name}: {e.Message}");
        }

        return string.Join(" -> ", parts);
    }
}
