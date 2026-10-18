using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace Conversia.Web.IntegrationTests.Infrastructure;

/// <summary>
/// An outermost middleware of the Web under test, added through an <see cref="IStartupFilter"/> (the Web's own pipeline is untouched).
/// It gives the tests two things the pipeline does not: the moment a request has left the whole pipeline (so "nothing was reported"
/// can be asserted after the Web is done, not after a guess), and the request's <see cref="HttpContext"/> from inside a fault
/// injected into the API client (so a test can wait for the browser to abandon the request, or start the response before failing).
/// </summary>
internal sealed class RequestProbe
{
    private static readonly AsyncLocal<HttpContext?> InFlight = new();

    private readonly ConcurrentDictionary<string, TaskCompletionSource> _finished = new(StringComparer.Ordinal);

    /// <summary>The request being served by the code that is running now (null outside a request).</summary>
    public static HttpContext? Current => InFlight.Value;

    public void Install(IServiceCollection services)
        => services.AddTransient<IStartupFilter>(_ => new Filter(this));

    /// <summary>Completes when a request for exactly <paramref name="pathAndQuery"/> has left the pipeline, however it ended.</summary>
    public Task FinishedAsync(string pathAndQuery)
        => Signal(pathAndQuery).Task.WaitAsync(TestTimeouts.Signal);

    private TaskCompletionSource Signal(string pathAndQuery)
        => _finished.GetOrAdd(pathAndQuery, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

    private sealed class Filter(RequestProbe probe) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, pipeline) =>
            {
                var requested = context.Request.GetEncodedPathAndQuery(); // before the exception handler re-executes the request elsewhere
                InFlight.Value = context;
                try
                {
                    await pipeline();
                }
                finally
                {
                    probe.Signal(requested).TrySetResult();
                }
            });
            next(app);
        };
    }
}
