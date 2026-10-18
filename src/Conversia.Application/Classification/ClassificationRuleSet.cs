using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Conversia.Application.Classification;

/// <summary>
/// The dictionaries of the rule-based classifier, as data. The default is the embedded <c>ClassificationRules.es-CO.json</c>; a deployment
/// that serves another vertical or country points <c>Classification:RulesPath</c> at its own file with the same shape. Phrases are written
/// in plain Spanish (see the comments of the default file for the little syntax they have) and normalized when the classifier is built.
/// </summary>
public sealed partial class ClassificationRuleSet
{
    private const string DefaultResourceName = "Conversia.Application.Classification.ClassificationRules.es-CO.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    public int Version { get; set; }

    public string? Locale { get; set; }

    public string? Vertical { get; set; }

    /// <summary>Whole-word replacements for abbreviations and typos of the customers' text ("q" is "que"). Applied to messages, never to phrases.</summary>
    public Dictionary<string, string> Aliases { get; set; } = [];

    public List<string> Negators { get; set; } = [];

    /// <summary>How many words before a phrase, in its clause, a negator may stand to negate it.</summary>
    public int NegationWindow { get; set; } = 4;

    public List<string> ClauseBreakers { get; set; } = [];

    public List<CodedPhrases> Services { get; set; } = [];

    public List<CodedPhrases> GeneralServices { get; set; } = [];

    public List<CodedPhrases> InterestReasons { get; set; } = [];

    public ObjectionPhrases Objections { get; set; } = new();

    public List<string> Competitor { get; set; } = [];

    public List<string> Inquiry { get; set; } = [];

    public List<string> BookingIntent { get; set; } = [];

    public CustomerConfirmationPhrases CustomerConfirmation { get; set; } = new();

    public List<string> BookingProposal { get; set; } = [];

    public List<string> AdvisorConfirmation { get; set; } = [];

    public List<string> Refusal { get; set; } = [];

    public List<string> Cancellation { get; set; } = [];

    /// <summary>The rules shipped with the application: Spanish (Colombia), laser hair removal.</summary>
    public static ClassificationRuleSet LoadDefault()
    {
        using var stream = typeof(ClassificationRuleSet).Assembly.GetManifestResourceStream(DefaultResourceName)
            ?? throw new InvalidOperationException("The default classification rules are missing from the assembly.");
        return Parse(stream, "the default rules");
    }

    /// <summary>Loads a rule file. Throws <see cref="InvalidOperationException"/> naming the problem; the message never quotes the file's content.</summary>
    public static ClassificationRuleSet LoadFromFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Parse(stream, "the rules file");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"The classification rules file could not be read ({ex.GetType().Name}).");
        }
    }

    public static ClassificationRuleSet Parse(Stream json, string source)
    {
        ClassificationRuleSet? rules;
        try
        {
            rules = JsonSerializer.Deserialize<ClassificationRuleSet>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"{Capitalize(source)} is not valid JSON of the expected shape (line {ex.LineNumber}).");
        }

        if (rules is null)
        {
            throw new InvalidOperationException($"{Capitalize(source)} is empty.");
        }

        rules.Validate(source);
        return rules;
    }

    /// <summary>Throws <see cref="InvalidOperationException"/> when a section the classifier cannot work without is missing or a code is malformed.</summary>
    public void Validate(string source = "the rules")
    {
        void Require(bool ok, string what)
        {
            if (!ok)
            {
                throw new InvalidOperationException($"{Capitalize(source)}: {what}");
            }
        }

        Require(Version == 1, "version must be 1.");
        Require(Negators.Count > 0, "negators must not be empty.");
        Require(NegationWindow is >= 1 and <= 10, "negationWindow must be between 1 and 10.");
        Require(Services.Count > 0, "services must not be empty.");
        Require(BookingIntent.Count > 0, "bookingIntent must not be empty.");
        Require(CustomerConfirmation.Strong.Count > 0, "customerConfirmation.strong must not be empty.");
        Require(AdvisorConfirmation.Count > 0, "advisorConfirmation must not be empty.");
        Require(Refusal.Count > 0, "refusal must not be empty.");

        foreach (var (name, list) in new (string, IReadOnlyList<CodedPhrases>)[] { ("services", Services), ("generalServices", GeneralServices), ("interestReasons", InterestReasons) })
        {
            var codes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in list)
            {
                Require(CodePattern().IsMatch(entry.Code ?? string.Empty), $"every code in {name} must be 1 to 40 lower-case letters, digits or '_'.");
                Require(codes.Add(entry.Code!), $"codes in {name} must be unique.");
                Require(entry.Phrases.Count > 0, $"every entry of {name} needs phrases.");
            }
        }

        foreach (var (name, list) in AllPhraseLists())
        {
            Require(list.All(p => !string.IsNullOrWhiteSpace(p)), $"{name} must not hold blank phrases.");
        }
    }

    private IEnumerable<(string Name, IReadOnlyList<string> Phrases)> AllPhraseLists()
    {
        yield return ("competitor", Competitor);
        yield return ("inquiry", Inquiry);
        yield return ("bookingIntent", BookingIntent);
        yield return ("bookingProposal", BookingProposal);
        yield return ("advisorConfirmation", AdvisorConfirmation);
        yield return ("refusal", Refusal);
        yield return ("cancellation", Cancellation);
        yield return ("negators", Negators);
        yield return ("clauseBreakers", ClauseBreakers);
        yield return ("objections.price", Objections.Price);
        yield return ("objections.timing", Objections.Timing);
        yield return ("objections.location", Objections.Location);
        yield return ("objections.trust", Objections.Trust);
        yield return ("objections.pain", Objections.Pain);
        yield return ("objections.other", Objections.Other);
        yield return ("customerConfirmation.strong", CustomerConfirmation.Strong);
        yield return ("customerConfirmation.weakAck", CustomerConfirmation.WeakAck);
        foreach (var entry in Services.Concat(GeneralServices).Concat(InterestReasons))
        {
            yield return ($"phrases of {entry.Code}", entry.Phrases);
        }
    }

    private static string Capitalize(string text) => char.ToUpperInvariant(text[0]) + text[1..];

    [GeneratedRegex("^[a-z0-9_]{1,40}$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();

    public sealed class CodedPhrases
    {
        public string? Code { get; set; }

        /// <summary>When mentioned, this entry replaces the other services of the same conversation.</summary>
        public bool Exclusive { get; set; }

        public List<string> Phrases { get; set; } = [];
    }

    public sealed class ObjectionPhrases
    {
        public List<string> Price { get; set; } = [];

        public List<string> Timing { get; set; } = [];

        public List<string> Location { get; set; } = [];

        public List<string> Trust { get; set; } = [];

        public List<string> Pain { get; set; } = [];

        public List<string> Other { get; set; } = [];
    }

    public sealed class CustomerConfirmationPhrases
    {
        public List<string> Strong { get; set; } = [];

        public List<string> WeakAck { get; set; } = [];
    }
}
