using System.Reflection;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;

namespace Conversia.Web.IntegrationTests.Infrastructure;

/// <summary>
/// Makes a service of a host fail on purpose, the way a bug in it would: every call goes through to the real service unless the
/// fault returns an exception for it, which is then thrown from the call. Used on the real API (say, its product service) so that an
/// unexpected failure happens where it really happens, without the Web's help. A minimal, generic counterpart of the fault
/// injection of the API's own test project (<c>ErrorTestHost</c>), which is internal to that project.
/// </summary>
internal static class ServiceFaults
{
    /// <summary>Replaces the registration of <typeparamref name="TService"/> by one that asks <paramref name="fault"/> before each call.</summary>
    public static void Decorate<TService>(IServiceCollection services, Func<string, object?[], Exception?> fault)
        where TService : class
    {
        var original = services.Last(d => d.ServiceType == typeof(TService));
        services.Remove(original);
        services.Add(new ServiceDescriptor(
            typeof(TService),
            provider =>
            {
                var inner = original.ImplementationFactory is { } factory
                    ? (TService)factory(provider)
                    : (TService)ActivatorUtilities.CreateInstance(provider, original.ImplementationType!);
                var proxy = DispatchProxy.Create<TService, FaultProxy<TService>>();
                var typed = (FaultProxy<TService>)(object)proxy;
                typed.Inner = inner;
                typed.Fault = fault;
                return proxy;
            },
            original.Lifetime));
    }

    public class FaultProxy<TService> : DispatchProxy
        where TService : class
    {
        public TService Inner { get; set; } = default!;

        public Func<string, object?[], Exception?> Fault { get; set; } = (_, _) => null;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (Fault(targetMethod!.Name, args ?? []) is { } failure)
            {
                throw failure;
            }

            try
            {
                return targetMethod.Invoke(Inner, args);
            }
            catch (TargetInvocationException wrapped)
            {
                ExceptionDispatchInfo.Capture(wrapped.InnerException!).Throw();
                throw;
            }
        }
    }
}
