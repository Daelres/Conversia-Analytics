using System.Net;
using Conversia.Application.Time;
using Conversia.Domain.Enums;
using Conversia.Web.Dashboard;
using Conversia.Web.Time;
using Microsoft.Extensions.DependencyInjection;

namespace Conversia.Web.IntegrationTests.Infrastructure;

/// <summary>
/// A Web host in front of a <see cref="MetricsStub"/> and a frozen clock: the period the page asks for is known to the stub, so the
/// answers (and the comparison with the period before) are deterministic without a fixed date in any test.
/// </summary>
internal sealed class PanelHarness : IDisposable
{
    private PanelHarness(WebTestHost host, FakeBackend backend, MetricsStub stub, DateRange range)
    {
        Host = host;
        Backend = backend;
        Stub = stub;
        Range = range;
    }

    public WebTestHost Host { get; }

    public FakeBackend Backend { get; }

    public MetricsStub Stub { get; }

    /// <summary>The local dates of the default period (the last 30 days) on the frozen clock.</summary>
    public DateRange Range { get; }

    /// <param name="make">Builds the stub for the period that ends (exclusive) on the date it is given.</param>
    /// <param name="member">The caller's permissions; null is an owner.</param>
    public static PanelHarness Create(Func<DateOnly, MetricsStub> make, IReadOnlyList<Permission>? member = null)
    {
        var clock = new ManualTimeProvider();
        var today = new TenantTimeZone(TenantTimeZone.DefaultId).Today(clock);
        var range = PeriodPresets.Resolve(PeriodPresets.Default, today);
        var stub = make(range.To);
        stub.MemberPermissions = member;

        var backend = new FakeBackend { Data = stub.Handle };
        var host = WebTestHost.CreateWithBackend(
            backend,
            WebSettings.With(("ErrorReporting:ApiKey", WebScenario.Key)),
            services => services.AddSingleton<TimeProvider>(clock));
        return new PanelHarness(host, backend, stub, range);
    }

    public async Task<WebSession> SignInAsync(string email = "owner@example.test")
        => await WebFlows.SignedInAsync(Host, email, WebScenario.Password);

    public async Task<PageResponse> OpenAsync(string query = "")
    {
        using var session = await SignInAsync();
        return await session.GetAsync("/panel" + query);
    }

    public void Dispose() => Host.Dispose();
}
