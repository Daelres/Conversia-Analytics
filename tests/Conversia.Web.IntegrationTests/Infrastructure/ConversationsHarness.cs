using Conversia.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;

namespace Conversia.Web.IntegrationTests.Infrastructure;

/// <summary>A Web host in front of a <see cref="ConversationsStub"/> and a frozen clock.</summary>
internal sealed class ConversationsHarness : IDisposable
{
    private ConversationsHarness(WebTestHost host, FakeBackend backend, ConversationsStub stub, ManualTimeProvider clock)
    {
        Host = host;
        Backend = backend;
        Stub = stub;
        Clock = clock;
    }

    public WebTestHost Host { get; }

    public FakeBackend Backend { get; }

    public ConversationsStub Stub { get; }

    public ManualTimeProvider Clock { get; }

    /// <param name="member">The caller's permissions; null is an owner.</param>
    public static ConversationsHarness Create(IReadOnlyList<Permission>? member = null, Action<ConversationsStub>? configure = null)
    {
        var clock = new ManualTimeProvider();
        var stub = new ConversationsStub(clock.GetUtcNow().UtcDateTime) { MemberPermissions = member };
        configure?.Invoke(stub);
        var backend = new FakeBackend { Data = stub.Handle };
        var host = WebTestHost.CreateWithBackend(
            backend,
            WebSettings.With(("ErrorReporting:ApiKey", WebScenario.Key)),
            services => services.AddSingleton<TimeProvider>(clock));
        return new ConversationsHarness(host, backend, stub, clock);
    }

    public Task<WebSession> SignInAsync(string email = "owner@example.test") => WebFlows.SignedInAsync(Host, email, WebScenario.Password);

    public async Task<PageResponse> OpenAsync(string url)
    {
        using var session = await SignInAsync();
        return await session.GetAsync(url);
    }

    /// <summary>The conversation API calls the Web made, in order, without the profile and the organization lookups.</summary>
    public IReadOnlyList<BackendRequest> ConversationCalls
        => Backend.DataRequests.Where(r => r.Path.StartsWith("/api/conversations", StringComparison.Ordinal)).ToList();

    public void Dispose() => Host.Dispose();
}
