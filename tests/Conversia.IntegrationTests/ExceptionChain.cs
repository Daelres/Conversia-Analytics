namespace Conversia.IntegrationTests;

/// <summary>Walks an exception chain (including aggregates) looking for a typed match, as the startup suites do.</summary>
internal static class ExceptionChain
{
    public static T? Find<T>(Exception root, Func<T, bool> predicate) where T : Exception
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
                    var found = Find(inner, predicate);
                    if (found is not null)
                    {
                        return found;
                    }
                }
            }
        }

        return null;
    }

    public static string Describe(Exception root)
    {
        var parts = new List<string>();
        for (var e = root; e is not null; e = e.InnerException)
        {
            parts.Add($"{e.GetType().Name}: {e.Message}");
        }

        return string.Join(" -> ", parts);
    }

    public static IEnumerable<Exception> Flatten(Exception root)
    {
        for (var e = root; e is not null; e = e.InnerException)
        {
            yield return e;
            if (e is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions.SelectMany(Flatten))
                {
                    yield return inner;
                }
            }
        }
    }
}
