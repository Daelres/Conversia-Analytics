using Conversia.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;

namespace Conversia.Web.IntegrationTests.Infrastructure;

/// <summary>A Web host in front of a <see cref="MessagesStub"/> and a frozen clock.</summary>
internal sealed class MessagesHarness : IDisposable
{
    private MessagesHarness(WebTestHost host, FakeBackend backend, MessagesStub stub, ManualTimeProvider clock)
    {
        Host = host;
        Backend = backend;
        Stub = stub;
        Clock = clock;
    }

    public WebTestHost Host { get; }

    public FakeBackend Backend { get; }

    public MessagesStub Stub { get; }

    public ManualTimeProvider Clock { get; }

    /// <param name="member">The caller's permissions; null is an owner.</param>
    public static MessagesHarness Create(IReadOnlyList<Permission>? member = null, Action<MessagesStub>? configure = null)
    {
        var clock = new ManualTimeProvider();
        var stub = new MessagesStub(clock.GetUtcNow().UtcDateTime) { MemberPermissions = member };
        configure?.Invoke(stub);
        var backend = new FakeBackend { Data = stub.Handle };
        var host = WebTestHost.CreateWithBackend(
            backend,
            WebSettings.With(("ErrorReporting:ApiKey", WebScenario.Key)),
            services => services.AddSingleton<TimeProvider>(clock));
        return new MessagesHarness(host, backend, stub, clock);
    }

    public Task<WebSession> SignInAsync(string email = "owner@example.test") => WebFlows.SignedInAsync(Host, email, WebScenario.Password);

    public async Task<PageResponse> OpenAsync(string url)
    {
        using var session = await SignInAsync();
        return await session.GetAsync(url);
    }

    public IReadOnlyList<BackendRequest> FeedCalls
        => Backend.DataRequests.Where(r => r.Path.StartsWith("/api/messages", StringComparison.Ordinal) || r.Path.StartsWith("/api/webhook-events", StringComparison.Ordinal)).ToList();

    public void Dispose() => Host.Dispose();
}
