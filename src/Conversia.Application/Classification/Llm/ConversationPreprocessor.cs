using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Conversia.Domain.Enums;

namespace Conversia.Application.Classification.Llm;

/// <summary>What the preprocessor did to one conversation, for the log of each LLM call. Counts and sizes only, never text.</summary>
/// <param name="MessagesIn">Messages considered (the latest <see cref="RuleBasedConversationClassifier.MaxMessages"/>).</param>
/// <param name="MessagesDropped">Messages without text (media without caption, blank).</param>
/// <param name="MessagesCollapsed">Messages whose repeated characters, emoji runs or symbols were collapsed.</param>
/// <param name="MessagesDeduplicated">Advisor messages replaced by a back-reference to an earlier identical one.</param>
/// <param name="MessagesTruncated">Advisor messages cut to <see cref="ConversationPreprocessorOptions.AdvisorMaxCharacters"/>.</param>
/// <param name="MessagesMerged">Messages that share a line with the message before them.</param>
/// <param name="MessagesOmittedByBudget">Messages left out to fit <see cref="ConversationPreprocessorOptions.TokenBudget"/>.</param>
/// <param name="OmittedIndices">The numbers of those messages.</param>
/// <param name="CharactersBefore">Size of the uncompressed block (every message as <c>[#n][C] text</c>, 1,000-character cap).</param>
/// <param name="CharactersAfter">Size of the block that is sent.</param>
public sealed record PreprocessingStats(
    int MessagesIn,
    int MessagesDropped,
    int MessagesCollapsed,
    int MessagesDeduplicated,
    int MessagesTruncated,
    int MessagesMerged,
    int MessagesOmittedByBudget,
    int UrlsReplaced,
    IReadOnlyList<int> OmittedIndices,
    int CharactersBefore,
    int CharactersAfter,
    int EstimatedTokensBefore,
    int EstimatedTokensAfter);

/// <param name="Entries">The lines (and gap) of the conversation block, in order.</param>
/// <param name="LastLine">The highest message number (the snapshot's message count): a cited line must be within 1..LastLine.</param>
public sealed record PreprocessedConversation(IReadOnlyList<PromptEntry> Entries, PreprocessingStats Stats, int LastLine)
{
    /// <summary>The numbers of the messages shown in the block: the only lines the model may cite.</summary>
    public IReadOnlySet<int> PresentNumbers { get; } = Entries.OfType<PromptLine>().SelectMany(l => l.Segments).Select(s => s.Number).ToHashSet();
}

/// <summary>
/// Spends as few tokens as possible on a conversation without losing what the reading depends on. Pure and deterministic; it runs on the
/// <b>pseudonymized</b> snapshot right before <see cref="LlmPromptBuilder"/>, and changes only what the model is sent (the rules classifier and
/// the Won guard keep reading the full real snapshot). In order:
/// <list type="number">
/// <item>Only the latest <see cref="RuleBasedConversationClassifier.MaxMessages"/> messages; customer messages keep their first 1,000 characters.</item>
/// <item>Messages without text are dropped (their number is simply skipped).</item>
/// <item>Each body is neutralized (<see cref="PromptTextNeutralizer"/>: white space collapsed), URLs become <c>[URL]</c>, a character repeated more
/// than three times keeps three (<c>siiiii</c>, <c>!!!!!!</c>), a run of emoji keeps three, and a message of only symbols keeps its first three.</item>
/// <item>An advisor message identical to an earlier one (ignoring case, accents, punctuation, emoji and spacing: copied templates, price lists,
/// greetings sent again) becomes a back-reference <c>=n</c> to the first. Customer messages are never collapsed this way.</item>
/// <item>A long advisor message keeps its beginning, its end (where a confirmation usually sits) and every clause that holds a phrase the rules
/// classifier reads in a business message (services, proposals, days and hours, confirmations); the dropped clauses become <see cref="PromptFormat.CutMarker"/>.</item>
/// <item>Consecutive messages of the same side share one line (always shorter than one line each), and every message keeps its number (see <see cref="PromptFormat"/>).</item>
/// <item>Over the token budget, the line of the first customer message (origin and interest) and the latest lines are always kept, then
/// the most recent lines that fit; what is left out is marked with a gap.</item>
/// </list>
/// Bump <see cref="Version"/> whenever the output for the same input changes: it is part of the prompt version and the snapshot hash.
/// </summary>
public sealed partial class ConversationPreprocessor
{
    public const int Version = 1;

    /// <summary>A repeated advisor message shorter than this is kept: a back-reference would save nothing worth the indirection.</summary>
    private const int MinBackReferenceLength = 12;

    private const int MaxRun = 3;

    private readonly ConversationPreprocessorOptions _options;
    private readonly (string Phrase, bool Prefix)[] _advisorSignals;

    /// <param name="rules">
    /// The deployment's rules: a cut never drops a clause holding a phrase the rules classifier reads in a business message (a service, a
    /// proposal, a day or an hour, a confirmation), so the model is never sent less than the rules read.
    /// </param>
    public ConversationPreprocessor(ConversationPreprocessorOptions options, ClassificationRuleSet rules)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(rules);
        options.Validate();
        _options = options;
        _advisorSignals = [.. rules.Services.Concat(rules.GeneralServices).SelectMany(s => s.Phrases)
            .Concat(rules.BookingProposal).Concat(rules.ProposalCue).Concat(rules.DayTime).Concat(rules.AdvisorConfirmation).Concat(rules.AdvisorScheduleStatement)
            .Select(p => p.Trim())
            .Select(p => (Phrase: TextNormalizer.Normalize(p.Trim('^', '$', ',', '*')), Prefix: p.EndsWith('*')))
            .Where(p => p.Phrase.Length > 0)
            .Distinct()];
    }

    public PreprocessedConversation Process(ConversationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var all = snapshot.Messages;
        var first = Math.Max(0, all.Count - RuleBasedConversationClassifier.MaxMessages);
        int dropped = 0, collapsed = 0, deduplicated = 0, truncated = 0, urls = 0, before = 0;
        var seenAdvisor = new Dictionary<string, int>(StringComparer.Ordinal);
        var prepared = new Dictionary<int, string>();
        var kept = new List<(PromptRole Role, PromptSegment Segment)>();

        for (var i = first; i < all.Count; i++)
        {
            var message = all[i];
            var number = i + 1;
            var role = message.Direction == MessageDirection.Inbound ? PromptRole.Customer : PromptRole.Advisor;
            var raw = CapAtWord(message.Text ?? string.Empty, RuleBasedConversationClassifier.MaxCharactersPerMessage);

            before += $"[#{number}][{PromptFormat.RoleCode(role)}] ".Length + PromptTextNeutralizer.Neutralize(raw).Length + (i > first ? 1 : 0);

            if (string.IsNullOrWhiteSpace(raw))
            {
                dropped++;
                continue;
            }

            var text = PromptTextNeutralizer.Neutralize(raw);
            text = Url().Replace(text, m =>
            {
                urls++;
                return "[URL]";
            });

            var compact = CollapseRuns(text);
            if (compact != text)
            {
                collapsed++;
                text = compact;
            }

            if (role == PromptRole.Advisor)
            {
                var key = TextNormalizer.Normalize(text);
                if (text.Length >= MinBackReferenceLength && key.Length > 0 && seenAdvisor.TryGetValue(key, out var original))
                {
                    deduplicated++;
                    text = PromptFormat.BackReference(original);
                }
                else
                {
                    if (key.Length > 0)
                    {
                        seenAdvisor.TryAdd(key, number);
                    }

                    if (text.Length > _options.AdvisorMaxCharacters)
                    {
                        var cut = Cut(text, _options.AdvisorMaxCharacters);
                        if (cut.Length < text.Length)
                        {
                            truncated++;
                            text = cut;
                        }
                    }

                    prepared[number] = text;
                }
            }

            kept.Add((role, new PromptSegment(number, text)));
        }

        var lines = Merge(kept, out var merged);
        var (entries, omitted) = FitBudget(lines);
        entries = ExpandDanglingReferences(entries, omitted, prepared);
        var after = PromptFormat.RenderLines(entries).Length;

        var stats = new PreprocessingStats(
            MessagesIn: all.Count - first,
            MessagesDropped: dropped,
            MessagesCollapsed: collapsed,
            MessagesDeduplicated: deduplicated,
            MessagesTruncated: truncated,
            MessagesMerged: merged,
            MessagesOmittedByBudget: omitted.Count,
            UrlsReplaced: urls,
            OmittedIndices: omitted,
            CharactersBefore: before,
            CharactersAfter: after,
            EstimatedTokensBefore: PromptFormat.EstimateTokens(before),
            EstimatedTokensAfter: PromptFormat.EstimateTokens(after));
        return new PreprocessedConversation(entries, stats, all.Count);
    }

    // ---- Text ---------------------------------------------------------------------------------------------------------

    // The first `max` characters; a word cut in half at the end is dropped (it could be the first digits of a number, or half a placeholder).
    private static string CapAtWord(string text, int max)
    {
        if (text.Length <= max)
        {
            return text;
        }

        var capped = text[..max];
        if (char.IsWhiteSpace(text[max]) || char.IsWhiteSpace(capped[^1]))
        {
            return capped;
        }

        var space = capped.LastIndexOfAny([' ', '\n', '\t', '\r']);
        return space > 0 ? capped[..space] : string.Empty;
    }

    // A back-reference to a message the budget left out would point at nothing: it shows that message's text instead.
    private static List<PromptEntry> ExpandDanglingReferences(List<PromptEntry> entries, List<int> omitted, Dictionary<int, string> prepared)
    {
        if (omitted.Count == 0)
        {
            return entries;
        }

        var gone = omitted.ToHashSet();
        return [.. entries.Select(e => e is PromptLine line
            ? line with
            {
                Segments = [.. line.Segments.Select(s => s.Text.Length > 1 && s.Text[0] == '=' && int.TryParse(s.Text.AsSpan(1), out var target) && gone.Contains(target)
                    ? s with { Text = prepared[target] }
                    : s)],
            }
            : e)];
    }

    // Identical text elements beyond three in a row go, so do emoji beyond three in a row (of any mix); a message of only symbols keeps three.
    private static string CollapseRuns(string text)
    {
        var builder = new StringBuilder(text.Length);
        string? previous = null;
        var sameRun = 0;
        var emojiRun = 0;
        var elements = 0;
        var symbolsOnly = true;
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            var element = enumerator.GetTextElement();
            sameRun = element == previous ? sameRun + 1 : 1;
            emojiRun = IsEmoji(element) ? emojiRun + 1 : 0;
            previous = element;
            if (sameRun > MaxRun || emojiRun > MaxRun)
            {
                continue;
            }

            symbolsOnly &= !element.Any(char.IsLetterOrDigit) && !char.IsWhiteSpace(element[0]);
            builder.Append(element);
            elements++;
        }

        if (symbolsOnly && elements > MaxRun)
        {
            var kept = new StringBuilder();
            var e = StringInfo.GetTextElementEnumerator(builder.ToString());
            for (var n = 0; n < MaxRun && e.MoveNext(); n++)
            {
                kept.Append(e.GetTextElement());
            }

            return kept.ToString();
        }

        return builder.ToString();
    }

    private static bool IsEmoji(string element)
        => element.Any(c => char.IsSurrogate(c) || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.OtherSymbol);

    // Clause by clause: the beginning (two thirds of the room), the end (one third) and every clause that holds a signal of the rules are
    // kept, each run of dropped clauses becomes the marker. A message without clause breaks is cut by characters around the marker.
    private string Cut(string text, int max)
    {
        var clauses = Clause().Matches(text).Select(m => m.Value).ToList();
        if (clauses.Count < 3)
        {
            return CutCharacters(text, max);
        }

        var keep = clauses.Select(HoldsSignal).ToArray();
        var room = max * 2 / 3;
        for (var i = 0; i < clauses.Count && clauses[i].Length <= room; i++)
        {
            room -= clauses[i].Length;
            keep[i] = true;
        }

        room = max - (max * 2 / 3);
        for (var i = clauses.Count - 1; i >= 0 && clauses[i].Length <= room; i--)
        {
            room -= clauses[i].Length;
            keep[i] = true;
        }

        if (keep.All(k => k))
        {
            return text;
        }

        var result = new StringBuilder();
        var dropping = false;
        for (var i = 0; i < clauses.Count; i++)
        {
            if (!keep[i])
            {
                dropping = true;
                continue;
            }

            if (dropping)
            {
                result.Append(result.Length > 0 ? " " : string.Empty).Append(PromptFormat.CutMarker).Append(' ');
                dropping = false;
            }

            result.Append(clauses[i]);
        }

        if (dropping)
        {
            result.Append(' ').Append(PromptFormat.CutMarker);
        }

        return PromptTextNeutralizer.CollapseWhitespace(result.ToString());
    }

    private bool HoldsSignal(string clause)
    {
        var normalized = " " + TextNormalizer.Normalize(clause) + " ";
        return _advisorSignals.Any(s => normalized.Contains(" " + s.Phrase + (s.Prefix ? string.Empty : " "), StringComparison.Ordinal));
    }

    // The beginning (two thirds of the room) and the end (one third), cut at word boundaries, around the marker.
    private static string CutCharacters(string text, int max)
    {
        var headLength = max * 2 / 3;
        var tailLength = max - headLength;
        var head = text[..headLength];
        var space = head.LastIndexOf(' ');
        if (space > headLength / 2)
        {
            head = head[..space];
        }

        var tail = text[^tailLength..];
        space = tail.IndexOf(' ');
        if (space >= 0 && space < tailLength / 2)
        {
            tail = tail[(space + 1)..];
        }

        return head.TrimEnd() + " " + PromptFormat.CutMarker + " " + tail.TrimStart();
    }

    // ---- Lines --------------------------------------------------------------------------------------------------------

    private static List<PromptLine> Merge(List<(PromptRole Role, PromptSegment Segment)> kept, out int merged)
    {
        merged = 0;
        var lines = new List<PromptLine>();
        List<PromptSegment>? current = null;
        var role = PromptRole.Customer;
        foreach (var (r, segment) in kept)
        {
            if (current is not null && r == role)
            {
                current.Add(segment);
                merged++;
                continue;
            }

            if (current is not null)
            {
                lines.Add(new PromptLine(role, current));
            }

            current = [segment];
            role = r;
        }

        if (current is not null)
        {
            lines.Add(new PromptLine(role, current));
        }

        return lines;
    }

    private (List<PromptEntry> Entries, List<int> Omitted) FitBudget(List<PromptLine> lines)
    {
        static int Tokens(IEnumerable<PromptEntry> entries) => PromptFormat.EstimateTokens(PromptFormat.RenderLines(entries).Length);

        if (Tokens(lines) <= _options.TokenBudget)
        {
            return ([.. lines], []);
        }

        var keep = new bool[lines.Count];
        var firstCustomer = lines.FindIndex(l => l.Role == PromptRole.Customer);
        if (firstCustomer >= 0)
        {
            keep[firstCustomer] = true;
        }

        for (var i = Math.Max(0, lines.Count - _options.KeepLastLines); i < lines.Count; i++)
        {
            keep[i] = true;
        }

        for (var i = lines.Count - 1; i >= 0; i--)
        {
            if (keep[i])
            {
                continue;
            }

            keep[i] = true;
            if (Tokens(Assemble(lines, keep, out _)) > _options.TokenBudget)
            {
                keep[i] = false;
                break; // the most recent lines that fit: an older one is never kept over a newer one
            }
        }

        return (Assemble(lines, keep, out var omitted), omitted);
    }

    private static List<PromptEntry> Assemble(List<PromptLine> lines, bool[] keep, out List<int> omitted)
    {
        omitted = [];
        var entries = new List<PromptEntry>();
        var gap = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            if (!keep[i])
            {
                gap += lines[i].Segments.Count;
                omitted.AddRange(lines[i].Segments.Select(s => s.Number));
                continue;
            }

            if (gap > 0)
            {
                entries.Add(new PromptGap(gap));
                gap = 0;
            }

            entries.Add(lines[i]);
        }

        if (gap > 0)
        {
            entries.Add(new PromptGap(gap));
        }

        return entries;
    }

    // A clause and the punctuation and space that end it.
    [GeneratedRegex(@"[^.,;:!?¡¿]+[.,;:!?¡¿]*\s*|[.,;:!?¡¿]+\s*", RegexOptions.CultureInvariant)]
    private static partial Regex Clause();

    [GeneratedRegex(@"\b(?:https?://|www\.)[^\s\[\]]*[^\s\[\].,;:!?¿¡)]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Url();
}
