using Conversia.Application.Classification;

namespace Conversia.Application.UnitTests.Classification;

public class TextTokensTests
{
    private static readonly IReadOnlyDictionary<string, string> NoAliases = new Dictionary<string, string>();
    private static readonly IReadOnlySet<string> NoBreakers = new HashSet<string>();

    private static TokenizedText Tokenize(string text, IReadOnlyDictionary<string, string>? aliases = null, IReadOnlySet<string>? breakers = null)
        => TextNormalizer.Tokenize(text, aliases ?? NoAliases, breakers ?? NoBreakers);

    // ---- Normalize ----------------------------------------------------------------------------

    [Theory]
    [InlineData("Hola", "hola")]
    [InlineData("HOLA!!!", "hola")]
    [InlineData("¿Cuánto cuesta la depilación?", "cuanto cuesta la depilacion")]
    [InlineData("Mañana, niño", "manana nino")]
    [InlineData("  varios   espacios  ", "varios espacios")]
    [InlineData("😍😍 quiero info 🙏", "quiero info")]
    [InlineData("2x1 en piernas", "2x1 en piernas")]
    [InlineData("$50.000 la sesión", "50 000 la sesion")]
    [InlineData("holaaaa", "hola")]
    [InlineData("siiiii", "si")]
    [InlineData("axilassss", "axilas")]
    [InlineData("carro", "carro")] // a pair stays: it is not a stretched word, and "caro" is not "carro"
    [InlineData("llamar", "llamar")]
    [InlineData("1000", "1000")] // digits are never collapsed
    [InlineData("", "")]
    [InlineData("😍", "")]
    public void Normalize_FoldsCaseAccentsPunctuationAndStretchedLetters(string text, string expected)
        => Assert.Equal(expected, TextNormalizer.Normalize(text));

    [Fact]
    public void Normalize_IsIdempotent()
    {
        foreach (var text in new[] { "¿Cuánto vale la depilación láser?", "HOLAAAA 😍", "Mañana 2x1" })
        {
            var once = TextNormalizer.Normalize(text);
            Assert.Equal(once, TextNormalizer.Normalize(once));
        }
    }

    // ---- Tokenize -----------------------------------------------------------------------------

    [Fact]
    public void Tokenize_SplitsSentencesAtDotsQuestionsAndNewLines_AndMarksQuestions()
    {
        var text = Tokenize("Hola. ¿Cuánto cuesta?\nGracias");

        Assert.Equal(["hola", "cuanto", "cuesta", "gracias"], text.Tokens.Select(t => t.Text));
        Assert.Equal([(0, 1, false), (1, 3, true), (3, 4, false)], text.Sentences.Select(s => (s.Start, s.End, s.IsQuestion)));
    }

    [Fact]
    public void Tokenize_CommasEndAClauseButNotASentence()
    {
        var text = Tokenize("no, gracias. si, quiero");

        Assert.Equal([0, 0, 1, 1], text.Tokens.Select(t => t.Sentence));
        Assert.NotEqual(text.Tokens[0].Clause, text.Tokens[1].Clause);
        Assert.NotEqual(text.Tokens[2].Clause, text.Tokens[3].Clause);
    }

    [Fact]
    public void Tokenize_AClauseBreakerEndsTheClauseAfterItself()
    {
        var text = Tokenize("no es caro pero lo pienso", breakers: new HashSet<string> { "pero" });

        var clauses = text.Tokens.Select(t => (t.Text, t.Clause)).ToList();
        Assert.Equal(clauses[0].Clause, clauses[3].Clause); // no es caro pero
        Assert.NotEqual(clauses[3].Clause, clauses[4].Clause); // lo pienso
    }

    [Fact]
    public void Tokenize_AppliesAliasesToWholeWordsOnly()
    {
        var text = Tokenize("q precio, qué tal", new Dictionary<string, string> { ["q"] = "que" });

        Assert.Equal(["que", "precio", "que", "tal"], text.Tokens.Select(t => t.Text));
    }

    [Fact]
    public void Tokenize_OfNothingMeaningful_IsEmpty()
    {
        Assert.Empty(Tokenize("").Tokens);
        Assert.Empty(Tokenize("😍🙏 ... ???").Tokens);
    }

    // ---- PhraseSet ----------------------------------------------------------------------------

    private static List<(int Start, int End)> Find(string phrase, string text)
        => [.. new PhraseSet([phrase]).Find(Tokenize(text)).Select(h => (h.Start, h.End))];

    [Fact]
    public void APhrase_MatchesWholeWordsInOrder()
    {
        Assert.Equal([(1, 3)], Find("me interesa", "no me interesa"));
        Assert.Empty(Find("me interesa", "interesa me"));
        Assert.Empty(Find("interes", "me interesa")); // not a prefix without the star
    }

    [Fact]
    public void ATrailingStar_LetsTheLastWordContinue()
    {
        Assert.Equal([(0, 1)], Find("pierna*", "piernas completas"));
        Assert.Equal([(2, 3)], Find("pierna*", "una sola pierna"));
        Assert.Empty(Find("pierna*", "pie"));
    }

    [Fact]
    public void Anchors_MeanTheStartAndTheEndOfASentence()
    {
        Assert.Single(Find("^ok$", "ok"));
        Assert.Empty(Find("^ok$", "ok gracias"));
        Assert.Empty(Find("^ok$", "dale ok"));
        Assert.Single(Find("^ok$", "Dale. Ok."));
        Assert.Single(Find("^ok gracias$", "ok, gracias"));
        Assert.Single(Find("^despues*", "despues te aviso"));
    }

    [Fact]
    public void APhrase_NeverCrossesASentence()
    {
        Assert.Empty(Find("no gracias", "no. gracias"));
        Assert.Single(Find("no gracias", "no, gracias")); // a comma only ends a clause
    }

    [Fact]
    public void APhraseThatNormalizesToNothing_IsRefused()
        => Assert.Throws<InvalidOperationException>(() => new PhraseSet(["???"]));
}
