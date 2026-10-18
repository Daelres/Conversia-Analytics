namespace Conversia.Web.IntegrationTests.Infrastructure;

/// <summary>Walks an exception and everything nested in it (host start-up wraps the original failure).</summary>
internal static class ExceptionChain
{
    public static T? Find<T>(Exception root, Func<T, bool> predicate) where T : Exception
        => Flatten(root).OfType<T>().FirstOrDefault(predicate);

    /// <summary>
    /// The way a host refuses a bad setting: an <see cref="InvalidOperationException"/> or an
    /// <see cref="Microsoft.Extensions.Options.OptionsValidationException"/> whose message names <paramref name="setting"/>.
    /// (An <see cref="ObjectDisposedException"/> is an <see cref="InvalidOperationException"/> too, but it is never a refusal.)
    /// </summary>
    public static Exception? FindRefusal(Exception root, string setting)
        => Flatten(root).FirstOrDefault(e => IsRefusalType(e) && e.Message.Contains(setting, StringComparison.Ordinal));

    /// <summary>
    /// Host start-up that was torn down under the failure: an <see cref="ObjectDisposedException"/> in the chain and no refusal
    /// of any kind next to it, so the real reason is lost and the boot is worth repeating.
    /// </summary>
    public static bool IsDisposalNoise(Exception root)
    {
        var chain = Flatten(root).ToList();
        return chain.Any(e => e is ObjectDisposedException)
            && !chain.Any(IsRefusalType);
    }

    private static bool IsRefusalType(Exception e)
        => e is not ObjectDisposedException and (InvalidOperationException or Microsoft.Extensions.Options.OptionsValidationException);

    public static string Describe(Exception root)
        => string.Join(" -> ", Flatten(root).Select(e => $"{e.GetType().Name}: {e.Message}"));

    public static IEnumerable<Exception> Flatten(Exception root)
    {
        var pending = new Stack<Exception>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            yield return current;
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    pending.Push(inner);
                }
            }
            else if (current.InnerException is not null)
            {
                pending.Push(current.InnerException);
            }
        }
    }
}
