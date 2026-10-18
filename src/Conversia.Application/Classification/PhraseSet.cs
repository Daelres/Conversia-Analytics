namespace Conversia.Application.Classification;

/// <summary>Where a phrase was found: the first token, one past the last, and the sentence.</summary>
internal readonly record struct PhraseHit(int Start, int End, int Sentence);

/// <summary>
/// A list of phrases compiled for matching on <see cref="TokenizedText"/>. A phrase is a run of words; a trailing <c>*</c> lets its last
/// word continue (<c>pierna*</c> matches <c>piernas</c>), a leading <c>^</c> makes it start a sentence, a trailing <c>$</c> end one and a
/// trailing <c>,</c> end its clause (<c>mejor no,</c> matches "mejor no, gracias" but not "mejor no rasurarme"). A phrase never crosses a
/// sentence boundary.
/// </summary>
internal sealed class PhraseSet
{
    // Phrases whose first word is spelled out, by that word; the (rare) phrases that are a single word with a trailing * are kept apart.
    private readonly Dictionary<string, Phrase[]> _byFirstWord;
    private readonly Phrase[] _singleWordPrefixes;
    private readonly int _count;

    public PhraseSet(IEnumerable<string> phrases)
    {
        var parsed = phrases.Select(Phrase.Parse).ToArray();
        _count = parsed.Length;
        _singleWordPrefixes = [.. parsed.Where(p => p.Words.Length == 1 && p.PrefixOfLast)];
        _byFirstWord = parsed
            .Where(p => !(p.Words.Length == 1 && p.PrefixOfLast))
            .GroupBy(p => p.Words[0], StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
    }

    public bool IsEmpty => _count == 0;

    /// <summary>Every place a phrase of the set is found, in text order (several phrases may start at the same word).</summary>
    public IEnumerable<PhraseHit> Find(TokenizedText text)
    {
        if (_count == 0)
        {
            yield break;
        }

        for (var s = 0; s < text.Sentences.Length; s++)
        {
            var sentence = text.Sentences[s];
            for (var start = sentence.Start; start < sentence.End; start++)
            {
                if (_byFirstWord.TryGetValue(text.Tokens[start].Text, out var candidates))
                {
                    foreach (var phrase in candidates)
                    {
                        if (phrase.MatchesAt(text.Tokens, start, sentence))
                        {
                            yield return new PhraseHit(start, start + phrase.Words.Length, s);
                        }
                    }
                }

                foreach (var phrase in _singleWordPrefixes)
                {
                    if (phrase.MatchesAt(text.Tokens, start, sentence))
                    {
                        yield return new PhraseHit(start, start + 1, s);
                    }
                }
            }
        }
    }

    private sealed record Phrase(string[] Words, bool PrefixOfLast, bool StartsSentence, bool EndsSentence, bool EndsClause)
    {
        public static Phrase Parse(string raw)
        {
            var text = raw.Trim();
            var starts = text.StartsWith('^');
            if (starts)
            {
                text = text[1..];
            }

            var ends = text.EndsWith('$');
            if (ends)
            {
                text = text[..^1];
            }

            var endsClause = !ends && text.EndsWith(',');
            if (endsClause)
            {
                text = text[..^1];
            }

            var prefix = text.EndsWith('*');
            if (prefix)
            {
                text = text[..^1];
            }

            var words = TextNormalizer.Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0)
            {
                throw new InvalidOperationException("A phrase of the classification rules is empty once normalized.");
            }

            return new Phrase(words, prefix, starts, ends, endsClause);
        }

        public bool MatchesAt(Token[] tokens, int start, Sentence sentence)
        {
            var end = start + Words.Length;
            if (end > sentence.End || (StartsSentence && start != sentence.Start) || (EndsSentence && end != sentence.End))
            {
                return false;
            }

            if (EndsClause && end != sentence.End && tokens[end].Clause == tokens[end - 1].Clause)
            {
                return false;
            }

            for (var i = 0; i < Words.Length; i++)
            {
                var word = tokens[start + i].Text;
                var last = i == Words.Length - 1;
                if (last && PrefixOfLast ? !word.StartsWith(Words[i], StringComparison.Ordinal) : word != Words[i])
                {
                    return false;
                }
            }

            return true;
        }
    }
}
