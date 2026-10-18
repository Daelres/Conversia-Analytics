using System.Text;
using Conversia.Application.Interfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Conversia.IntegrationTests;

/// <summary>
/// The API must refuse to boot with an unsafe <c>Jwt:SigningKey</c>: missing,
/// blank, shorter than 32 bytes (UTF-8) or equal to the placeholder shipped
/// in appsettings.json. The failure must be an <see cref="InvalidOperationException"/>
/// naming the setting without ever printing the key. These tests never touch
/// the database — the host must fail (or, for valid keys, build) before any DB access.
/// </summary>
public class JwtSigningKeyStartupTests
{
    private const string SigningKeySetting = "Jwt:SigningKey";
    private const string ShippedPlaceholder = "REPLACE_WITH_A_STRONG_SECRET_AT_LEAST_32_BYTES_LONG";

    /// <summary>
    /// Boots the API with only the signing key overridden. A <c>null</c> key means
    /// "not configured at all": the host runs from an empty content root so the
    /// placeholder in the API's appsettings.json is not picked up.
    /// </summary>
    private sealed class SigningKeyFactory(string? signingKey) : WebApplicationFactory<Program>
    {
        private readonly string _emptyContentRoot = Path.Combine(Path.GetTempPath(), $"jwt-startup-{Guid.NewGuid():N}");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // UseSetting (unlike ConfigureAppConfiguration) is visible to Program.cs
            // while it is still composing the host, which is when the key is read.
            if (signingKey is null)
            {
                Directory.CreateDirectory(_emptyContentRoot);
                builder.UseContentRoot(_emptyContentRoot);
            }
            else
            {
                builder.UseSetting(SigningKeySetting, signingKey);
            }
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

    public static TheoryData<string?, string> UnsafeSigningKeys => new()
    {
        { null, "missing" },
        { "", "empty" },
        { " ", "single space" },
        { new string(' ', 64), "64 spaces (long enough but blank)" },
        { "\t\r\n\t\r\n\t\r\n\t\r\n\t\r\n\t\r\n\t\r\n\t\r\n\t\r\n\t\r\n\t\r\n", "long whitespace-only mix" },
        { "short-key", "9 bytes" },
        { new string('k', 31), "31 bytes (one below the minimum)" },
        { new string('é', 15), "15 characters / 30 bytes in UTF-8" },
        { ShippedPlaceholder, "the placeholder shipped in appsettings.json" },
    };

    [Theory]
    [MemberData(nameof(UnsafeSigningKeys))]
    public void Boot_WithUnsafeSigningKey_FailsWithInvalidOperationExceptionNamingTheSetting(string? signingKey, string reason)
    {
        using var factory = new SigningKeyFactory(signingKey);

        var failure = Record.Exception(() => factory.CreateClient());

        Assert.True(failure is not null, $"Host booted with an unsafe signing key ({reason}).");
        var startupError = FindInChain<InvalidOperationException>(failure!, e => e.Message.Contains(SigningKeySetting));
        Assert.True(
            startupError is not null,
            $"Expected an InvalidOperationException naming '{SigningKeySetting}' ({reason}); got: {Describe(failure!)}");
    }

    [Theory]
    [InlineData("short-key")]
    [InlineData("REPLACE_WITH_A_STRONG_SECRET_AT_LEAST_32_BYTES_LONG")]
    [InlineData("0123456789abcdef0123456789abcde")]
    public void Boot_WithUnsafeSigningKey_NeverPrintsTheKeyInTheFailure(string signingKey)
    {
        using var factory = new SigningKeyFactory(signingKey);

        var failure = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(failure);
        for (var e = failure; e is not null; e = e.InnerException)
        {
            Assert.DoesNotContain(signingKey, e.Message);
            Assert.DoesNotContain(signingKey, e.ToString());
        }
    }

    public static TheoryData<string, string> AcceptableSigningKeys => new()
    {
        { new string('k', 32), "exactly 32 ASCII bytes" },
        { new string('é', 16), "16 characters but exactly 32 bytes in UTF-8" },
        { TestJwtSettings.SigningKey, "the 64-byte key used by the test suite" },
        { new string('k', 512), "very long key" },
    };

    [Theory]
    [MemberData(nameof(AcceptableSigningKeys))]
    public void Boot_WithSafeSigningKey_BuildsTheHostAndResolvesTokenService(string signingKey, string reason)
    {
        Assert.True(Encoding.UTF8.GetByteCount(signingKey) >= 32, "Test data error: key must be >= 32 bytes.");
        using var factory = new SigningKeyFactory(signingKey);

        var failure = Record.Exception(() =>
        {
            using var scope = factory.Services.CreateScope();
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<IJwtTokenService>());
        });

        Assert.True(failure is null, $"Host failed to boot with a safe key ({reason}): {failure}");
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
