using Conversia.TestSupport;
using Microsoft.Extensions.Logging;

namespace Conversia.IntegrationTests;

/// <summary>Regression tests for the findings of the error registry audit that need the real pipeline.</summary>
[Collection(nameof(IntegrationTestCollection))]
public class ErrorRegistryAuditTests
{
    private readonly ErrorTestHost _host;

    public ErrorRegistryAuditTests(CustomWebApplicationFactory shared) => _host = ErrorTestHost.Default(shared);

    [Fact]
    public async Task An_exception_whose_message_and_tostring_throw_still_gets_the_500_with_a_code()
    {
        var marker = ErrorTestHost.NewMarker();
        using var client = _host.NewClient();

        using var response = await client.SendAsync(ErrorTestHost.FailingLogin(marker, FaultInjection.HostileException));

        var code = await ErrorContract.AssertUnexpectedAsync(response, "exploded");
        var row = await ErrorLogDb.RequireByCodeAsync(_host.ConnectionString, code);
        Assert.EndsWith(nameof(HostileException), row.ExceptionType);
        Assert.DoesNotContain("exploded", row.ExceptionMessage ?? string.Empty);
        Assert.DoesNotContain("exploded", row.ExceptionDetails ?? string.Empty);
        Assert.Contains(_host.Logs.Entries, e => e.Level == LogLevel.Error && e.HasPropertyValue(code));
    }
}
