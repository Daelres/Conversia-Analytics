using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Conversia.Application.Classification;

/// <summary>A word of a message, with the sentence and the clause it belongs to. Sentences end at . ! ? and new lines; clauses also at commas and at "pero".</summary>
internal readonly record struct Token(string Text, int Sentence, int Clause);

internal readonly record struct Sentence(int Start, int End, bool IsQuestion);

/// <summary>A message split into words, sentences and clauses, ready for phrase matching.</summary>
internal sealed class TokenizedText
{
    public TokenizedText(Token[] tokens, Sentence[] sentences)
    {
        Tokens = tokens;
        Sentences = sentences;
    }

    public Token[] Tokens { get; }

    public Sentence[] Sentences { get; }

    public static TokenizedText Empty { get; } = new([], []);
}

internal static partial class TextNormalizer
{
    /// <summary>
    /// Lower case, no accents (the tilde of "ñ" goes too), everything that is not a letter or a digit becomes a space (punctuation, emoji),
    /// a letter repeated three times or more becomes one ("holaaa", "siiii"; "llamar" and "carro" keep their pairs), and words are
    /// separated by single spaces.
    /// </summary>
    public static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        var lastWasSpace = true;
        foreach (var raw in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(raw) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(raw))
            {
                builder.Append(char.ToLowerInvariant(raw));
                lastWasSpace = false;
            }
            else if (!lastWasSpace)
            {
                builder.Append(' ');
                lastWasSpace = true;
            }
        }

        return StretchedLetters().Replace(builder.ToString().TrimEnd(), "$1");
    }

    [GeneratedRegex(@"(\p{L})\1{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex StretchedLetters();

    /// <summary>Splits a message into tokens. <paramref name="aliases"/> are applied to whole words after normalization.</summary>
    public static TokenizedText Tokenize(string text, IReadOnlyDictionary<string, string> aliases, IReadOnlySet<string> clauseBreakers)
    {
        var tokens = new List<Token>();
        var sentences = new List<Sentence>();
        var sentence = 0;
        var clause = 0;
        var sentenceStart = 0;
        var segment = new StringBuilder();

        void FlushSegment()
        {
            if (segment.Length == 0)
            {
                return;
            }

            foreach (var word in Normalize(segment.ToString()).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var final = aliases.TryGetValue(word, out var alias) ? alias : word;
                tokens.Add(new Token(final, sentence, clause));
                if (clauseBreakers.Contains(final))
                {
                    clause++;
                }
            }

            segment.Clear();
        }

        void EndSentence(bool question)
        {
            FlushSegment();
            if (tokens.Count > sentenceStart)
            {
                sentences.Add(new Sentence(sentenceStart, tokens.Count, question));
                sentence++;
                clause++;
                sentenceStart = tokens.Count;
            }
        }

        foreach (var c in text)
        {
            switch (c)
            {
                case '.' or '!' or '¡' or '¿' or '\n' or '\r' or '…' or '(' or ')':
                    EndSentence(question: false);
                    break;
                case '?':
                    EndSentence(question: true);
                    break;
                case ',' or ';' or ':':
                    FlushSegment();
                    clause++;
                    break;
                default:
                    segment.Append(c);
                    break;
            }
        }

        EndSentence(question: false);
        return new TokenizedText([.. tokens], [.. sentences]);
    }
}
