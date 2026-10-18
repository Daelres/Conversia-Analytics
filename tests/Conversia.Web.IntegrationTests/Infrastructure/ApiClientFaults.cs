using System.Reflection;
using System.Runtime.ExceptionServices;
using Conversia.Web.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Conversia.Web.IntegrationTests.Infrastructure;

/// <summary>
/// Makes the Web fail the way a bug in the Web would (an exception that is not a transport error, which the real client turns into
/// "unavailable"): every call to the API client goes through to the real <see cref="ApiClient"/> unless <see cref="Fault"/> returns
/// an exception for it, which is then thrown from the call itself. The fault can be changed while a test runs.
/// </summary>
internal sealed class ApiClientFaults
{
    /// <summary>Given the name of the <see cref="IApiClient"/> method and its arguments, the exception to throw, or null to let the call through.</summary>
    public Func<string, object?[], Exception?>? Fault { get; set; }

    /// <summary>Wraps the <see cref="IApiClient"/> registration of the Web (use as <c>configureServices</c>).</summary>
    public void Install(IServiceCollection services)
    {
        var original = services.Last(d => d.ServiceType == typeof(IApiClient));
        services.Remove(original);
        services.Add(new ServiceDescriptor(
            typeof(IApiClient),
            provider => Wrap(Create(provider, original)),
            original.Lifetime));
    }

    public static ApiClientFaults Throwing(Func<string, object?[], Exception?> fault) => new() { Fault = fault };

    /// <summary>Every call throws <paramref name="exception"/>.</summary>
    public static ApiClientFaults Always(Func<Exception> exception) => new() { Fault = (_, _) => exception() };

    private static IApiClient Create(IServiceProvider provider, ServiceDescriptor original)
        => original.ImplementationFactory is { } factory
            ? (IApiClient)factory(provider)
            : (IApiClient)ActivatorUtilities.CreateInstance(provider, original.ImplementationType!);

    private IApiClient Wrap(IApiClient inner)
    {
        var proxy = DispatchProxy.Create<IApiClient, Proxy>();
        var typed = (Proxy)(object)proxy;
        typed.Inner = inner;
        typed.Owner = this;
        return proxy;
    }

    public class Proxy : DispatchProxy
    {
        public IApiClient Inner { get; set; } = default!;

        public ApiClientFaults Owner { get; set; } = default!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var fault = Owner.Fault?.Invoke(targetMethod!.Name, args ?? []);
            if (fault is not null)
            {
                throw fault;
            }

            try
            {
                return targetMethod!.Invoke(Inner, args);
            }
            catch (TargetInvocationException wrapped)
            {
                ExceptionDispatchInfo.Capture(wrapped.InnerException!).Throw();
                throw;
            }
        }
    }
}
