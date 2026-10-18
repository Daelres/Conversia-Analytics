using System.Text;

namespace Conversia.Application.ErrorLogging;

/// <summary>Reduces a stack-frame method name to its stable <c>Namespace.Type.Method</c> form.</summary>
internal static class StackFrameName
{
    public static string Normalize(ReadOnlySpan<char> name)
    {
        var withoutGenerics = StripGenericNoise(name);
        return StripCompilerGenerated(withoutGenerics);
    }

    // "Handler`1.Run[T,U]" -> "Handler.Run" (arity and generic arguments change with the instantiation, not with the defect).
    private static string StripGenericNoise(ReadOnlySpan<char> name)
    {
        var builder = new StringBuilder(name.Length);
        var depth = 0;
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (c == '[')
            {
                depth++;
            }
            else if (c == ']')
            {
                depth = Math.Max(0, depth - 1);
            }
            else if (depth == 0 && c == '`')
            {
                while (i + 1 < name.Length && char.IsAsciiDigit(name[i + 1]))
                {
                    i++;
                }
            }
            else if (depth == 0)
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    // "Svc.<LoginAsync>d__3.MoveNext" -> "Svc.LoginAsync"; "Svc.<>c__DisplayClass1_0.<Run>b__0" -> "Svc.Run".
    private static string StripCompilerGenerated(string name)
    {
        if (!name.Contains('<'))
        {
            return name;
        }

        var kept = new List<string>();
        foreach (var segment in name.Split('.'))
        {
            if (segment.StartsWith("<>", StringComparison.Ordinal))
            {
                continue;
            }

            kept.Add(OriginalName(segment));
        }

        if (kept.Count > 1 && kept[^1] == "MoveNext" && name.Contains(">d__", StringComparison.Ordinal))
        {
            kept.RemoveAt(kept.Count - 1);
        }

        return string.Join('.', kept);
    }

    private static string OriginalName(string segment)
    {
        var close = segment.IndexOf('>');
        return segment.StartsWith('<') && close > 1 ? segment[1..close] : segment;
    }
}
