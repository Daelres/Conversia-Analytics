namespace Conversia.Application.Classification;

/// <summary>Where a phrase was found: the first token, one past the last, and the sentence.</summary>
internal readonly record struct PhraseHit(int Start, int End, int Sentence);

/// <summary>
/// A list of phrases compiled for matching on <see cref="TokenizedText"/>. A phrase is a run of words; a trailing <c>*</c> lets its last
/// word continue (<c>pierna*</c> matches <c>piernas</c>), a leading <c>^</c> makes it start a sentence and a trailing <c>$</c> end one. A
/// phrase never crosses a sentence boundary.
/// </summary>
internal sealed class PhraseSet
{
    private readonly Phrase[] _phrases;

    public PhraseSet(IEnumerable<string> phrases)
    {
        _phrases = [.. phrases.Select(Phrase.Parse)];
    }

    public bool IsEmpty => _phrases.Length == 0;

    /// <summary>Every place a phrase of the set is found, in text order.</summary>
    public IEnumerable<PhraseHit> Find(TokenizedText text)
    {
        foreach (var sentence in text.Sentences.Select((s, i) => (Info: s, Index: i)))
        {
            for (var start = sentence.Info.Start; start < sentence.Info.End; start++)
            {
                foreach (var phrase in _phrases)
                {
                    if (phrase.MatchesAt(text.Tokens, start, sentence.Info))
                    {
                        yield return new PhraseHit(start, start + phrase.Words.Length, sentence.Index);
                    }
                }
            }
        }
    }

    private sealed record Phrase(string[] Words, bool PrefixOfLast, bool StartsSentence, bool EndsSentence)
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

            return new Phrase(words, prefix, starts, ends);
        }

        public bool MatchesAt(Token[] tokens, int start, Sentence sentence)
        {
            var end = start + Words.Length;
            if (end > sentence.End || (StartsSentence && start != sentence.Start) || (EndsSentence && end != sentence.End))
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
