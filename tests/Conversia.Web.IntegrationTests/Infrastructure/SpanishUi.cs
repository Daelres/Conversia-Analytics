using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using Conversia.TestSupport;
using Xunit.Sdk;

namespace Conversia.Web.IntegrationTests.Infrastructure;

/// <summary>One piece of user-visible text of a page and where it was found (for readable failure messages).</summary>
internal sealed record ScannedText(string Where, string Text);

/// <summary>
/// What a person can read on a page: the text of the document, plus the attributes browsers and assistive technology
/// show or announce (<c>&lt;title&gt;</c>, <c>title</c>, <c>placeholder</c>, <c>aria-label</c>, <c>alt</c>, the <c>value</c> of
/// submit/reset/button inputs, the meta description).
///
/// <para>Excluded as data or developer-facing, never as wording: <c>script</c>/<c>style</c>/<c>template</c>/<c>noscript</c>/<c>svg</c>,
/// the SKU and name cells of the product table (<c>data-field="sku|name"</c>, user-entered data), and every <c>data-*</c>
/// attribute (enum names such as <c>UsersManage</c> are identifiers, not text). E-mail addresses, URLs and technology names are
/// removed by <see cref="EnglishLeaks"/>' allowlist. Everything else is scanned, including text inside <c>hidden</c> elements,
/// which costs nothing to translate and may become visible.</para>
///
/// <para><c>code</c>/<c>pre</c>/<c>samp</c>/<c>kbd</c> are NOT skipped wholesale (English prose inside them is still English on screen).
/// <see cref="CodeText"/> removes only what is not wording, line by line: a route (optionally with an HTTP verb), a JSON line, a command
/// line, or a <c>kbd</c> made of key names. Whatever remains is scanned like any other text.</para>
///
/// <para>Besides the denylist, every scanned piece of text of four or more words must itself contain a Spanish word
/// (<see cref="EnglishLeaks.LooksSpanish"/>), so an English sentence that happens to use no denylisted word cannot hide
/// on a page that is otherwise Spanish.</para>
/// </summary>
internal static class SpanishUi
{
    private static readonly HashSet<string> SkippedElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "template", "noscript", "svg", "head"
    };

    private static readonly HashSet<string> CodeElements = new(StringComparer.OrdinalIgnoreCase) { "code", "pre", "kbd", "samp" };

    private static readonly string[] TextAttributes = { "title", "placeholder", "aria-label", "aria-description", "alt" };

    public static IReadOnlyList<ScannedText> Extract(PageResponse page)
    {
        var found = new List<ScannedText>();
        var document = page.Document;

        if (!string.IsNullOrWhiteSpace(document.Title))
        {
            found.Add(new ScannedText("<title>", document.Title));
        }

        foreach (var meta in document.QuerySelectorAll("meta[name=description][content]"))
        {
            found.Add(new ScannedText("meta description", meta.GetAttribute("content")!));
        }

        if (document.Body is { } body)
        {
            Walk(body, found);
        }

        return found;
    }

    /// <summary>All the scanned text joined, for the "is this Spanish and not empty" preconditions.</summary>
    public static string AllText(IEnumerable<ScannedText> scanned) => string.Join(" ", scanned.Select(s => s.Text));

    private static void Walk(INode node, List<ScannedText> found)
    {
        if (node is IElement element)
        {
            if (SkippedElements.Contains(element.LocalName) || IsUserData(element))
            {
                return;
            }

            foreach (var attribute in TextAttributes)
            {
                if (element.GetAttribute(attribute) is { Length: > 0 } value)
                {
                    found.Add(new ScannedText($"{element.LocalName}[{attribute}]", value));
                }
            }

            if (IsButtonLike(element) && element.GetAttribute("value") is { Length: > 0 } buttonValue)
            {
                found.Add(new ScannedText($"{element.LocalName}[value]", buttonValue));
            }
        }
        else if (node.NodeType == NodeType.Text && !string.IsNullOrWhiteSpace(node.TextContent))
        {
            var where = $"text in <{node.ParentElement?.LocalName}>";
            var code = CodeAncestor(node);
            if (code is not null)
            {
                found.AddRange(CodeText.Wording(code, node.TextContent).Select(line => new ScannedText(where, line)));
            }
            else
            {
                found.Add(new ScannedText(where, node.TextContent.Trim()));
            }
        }

        foreach (var child in node.ChildNodes)
        {
            Walk(child, found);
        }
    }

    /// <summary>The nearest enclosing code/pre/samp/kbd element (syntax-highlight spans sit inside one), or null.</summary>
    private static string? CodeAncestor(INode node)
    {
        for (var element = node.ParentElement; element is not null; element = element.ParentElement)
        {
            if (CodeElements.Contains(element.LocalName))
            {
                return element.LocalName;
            }
        }

        return null;
    }

    private static bool IsUserData(IElement element)
        => element.GetAttribute("data-field") is "sku" or "name";

    private static bool IsButtonLike(IElement element)
        => element is IHtmlInputElement { Type: "submit" or "reset" or "button" } || element is IHtmlButtonElement;

    /// <summary>Every denylisted English term in the scanned text, as "term in where: snippet" lines.</summary>
    public static IReadOnlyList<string> Leaks(IEnumerable<ScannedText> scanned)
        => scanned
            .SelectMany(s => EnglishLeaks.Find(s.Text).Select(term => $"\"{term}\" in {s.Where}: {Snippet(s.Text)}"))
            .ToList();

    /// <summary>The texts of four or more words (<see cref="MinimumSentenceWords"/>) that contain no Spanish word at all, as "where: snippet" lines.</summary>
    public static IReadOnlyList<string> NotSpanish(IEnumerable<ScannedText> scanned)
        => scanned
            .Where(s => EnglishLeaks.WordCount(s.Text) >= MinimumSentenceWords && !EnglishLeaks.LooksSpanish(s.Text))
            .Select(s => $"{s.Where}: {Snippet(s.Text)}")
            .ToList();

    public const int MinimumSentenceWords = 4;

    public static string Snippet(string text) => text.Length <= 90 ? text : text[..90] + "...";
}

/// <summary>
/// Collects the verdicts on many pages so one failing test reports every English leak at once. A page passes when it is
/// really a rendered page (a non-trivial amount of text that contains Spanish, a non-empty <c>&lt;title&gt;</c>),
/// declares <c>&lt;html lang="es"&gt;</c> and contains no denylisted English wording.
/// </summary>
internal sealed class SpanishUiReport
{
    private const int MinimumWords = 4;

    private readonly List<string> _problems = new();
    private readonly List<string> _checked = new();

    public int PagesChecked => _checked.Count;

    /// <summary>Checks one rendered page (any status) under a label that names the state it shows.</summary>
    public void Check(string label, PageResponse page)
    {
        _checked.Add(label);

        if (page.IsRedirect || string.IsNullOrWhiteSpace(page.Body))
        {
            _problems.Add($"[{label}] expected a rendered page but got status {page.Status} with {(string.IsNullOrWhiteSpace(page.Body) ? "no body" : "a redirect")}.");
            return;
        }

        var lang = page.Document.DocumentElement.GetAttribute("lang");
        if (!string.Equals(lang, "es", StringComparison.OrdinalIgnoreCase))
        {
            _problems.Add($"[{label}] <html lang> is '{lang ?? "(missing)"}', expected 'es'.");
        }

        if (string.IsNullOrWhiteSpace(page.Document.Title))
        {
            _problems.Add($"[{label}] the <title> is empty.");
        }

        var scanned = SpanishUi.Extract(page);
        var all = SpanishUi.AllText(scanned);
        if (EnglishLeaks.WordCount(all) < MinimumWords)
        {
            _problems.Add($"[{label}] only {EnglishLeaks.WordCount(all)} words were scanned (status {page.Status}); the page did not render what it should. Text: {SpanishUi.Snippet(page.Text)}");
        }
        else if (!EnglishLeaks.LooksSpanish(all))
        {
            _problems.Add($"[{label}] the scanned text contains no Spanish word at all: {SpanishUi.Snippet(all)}");
        }

        foreach (var leak in SpanishUi.Leaks(scanned))
        {
            _problems.Add($"[{label}] English wording {leak}");
        }

        foreach (var sentence in SpanishUi.NotSpanish(scanned))
        {
            _problems.Add($"[{label}] a text of {SpanishUi.MinimumSentenceWords}+ words has no Spanish word in it, {sentence}");
        }
    }

    /// <summary>Fails with every problem found, and when no page was checked at all (a scanner that scans nothing proves nothing).</summary>
    public void AssertClean()
    {
        if (_checked.Count == 0)
        {
            throw new XunitException("No page was checked.");
        }

        if (_problems.Count == 0)
        {
            return;
        }

        var message = new StringBuilder()
            .AppendLine($"{_problems.Count} problem(s) on {_checked.Count} page(s) checked ({string.Join(", ", _checked)}):");
        foreach (var problem in _problems)
        {
            message.AppendLine("  - " + problem);
        }

        throw new XunitException(message.ToString());
    }
}

/// <summary>
/// Decides which lines of a <c>code</c>/<c>pre</c>/<c>samp</c>/<c>kbd</c> text are not wording. The heuristic is positive identification
/// of developer-facing shapes (a line that merely looks short or technical is not exempt), applied per line:
/// <list type="bullet">
/// <item>a route, optionally preceded by an upper-case HTTP verb (<c>GET /api/users</c>, <c>/login</c>): the route is dropped, any text after it is scanned;</item>
/// <item>a lone HTTP verb (<c>DELETE</c>);</item>
/// <item>a JSON line: it starts with <c>{ } [ ] ,</c> or with <c>"key":</c> (JSON names are identifiers; example payload values are data);</item>
/// <item>a command line: optional <c>$</c> prompt, then a known tool (<c>dotnet</c>, <c>docker</c>, <c>git</c>, <c>curl</c>, ...) or <c>./script</c>; a trailing <c># comment</c> is scanned;</item>
/// <item>for <c>kbd</c>: only when every <c>+</c>/space separated token is a key name (<c>Ctrl+S</c>, <c>Enter</c>).</item>
/// </list>
/// A single identifier, a request id or the demo password need no rule: they match nothing on the denylist (whole-word matching).
/// Everything not recognised, including <c>&lt;code&gt;Sign in&lt;/code&gt;</c> and <c>&lt;pre&gt;Something went wrong&lt;/pre&gt;</c>, is returned to be scanned.
/// </summary>
internal static class CodeText
{
    private const string Verbs = "GET|POST|PUT|PATCH|DELETE|HEAD|OPTIONS";

    private static readonly Regex RoutePrefix = new($@"^(?:(?:{Verbs})\s+)?/\S*\s*", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex LoneVerb = new($@"^(?:{Verbs})$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex JsonLine = new(@"^(?:[\[\]{},]|""[^""]*""\s*:)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Command = new(@"^(?:\$\s+)?(?:(?:dotnet|docker|docker-compose|git|curl|wget|npm|npx|cd|bash|sh|pwsh|powershell|chmod|sqlcmd|sudo)|\./\S+)(?:\s|$)(?<rest>.*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex KeyName = new(@"^(?:Enter|Esc|Escape|Tab|Shift|Ctrl|Control|Alt|Cmd|Command|Space|Backspace|Delete|Del|Home|End|PageUp|PageDown|Up|Down|Left|Right|F\d{1,2}|[A-Za-z0-9])$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The parts of <paramref name="text"/> (an element's text) that are wording and must be scanned.</summary>
    public static IEnumerable<string> Wording(string element, string text)
    {
        if (string.Equals(element, "kbd", StringComparison.OrdinalIgnoreCase)
            && text.Split(new[] { '+', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).All(KeyName.IsMatch))
        {
            yield break;
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || LoneVerb.IsMatch(line) || JsonLine.IsMatch(line))
            {
                continue;
            }

            if (Command.Match(line) is { Success: true } command)
            {
                var rest = command.Groups["rest"].Value;
                var hash = rest.IndexOf('#');
                if (hash >= 0 && rest[(hash + 1)..].Trim() is { Length: > 0 } comment)
                {
                    yield return comment;
                }

                continue;
            }

            var remainder = RoutePrefix.Replace(line, string.Empty).Trim();
            if (remainder.Length > 0)
            {
                yield return remainder;
            }
        }
    }
}
