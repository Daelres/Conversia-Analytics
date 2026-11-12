namespace Conversia.Application.Classification.Llm;

/// <summary>
/// The settings of <see cref="ConversationPreprocessor"/>, section <c>Classification:Llm:Preprocessing</c>. Every value has a documented
/// default and a validated range (docs/CLASSIFICATION.md). They change what the model reads, so they are part of the snapshot hash
/// (<see cref="Fingerprint"/>): changing one invalidates the reuse of stored results.
/// </summary>
/// <param name="AdvisorMaxCharacters">Longest advisor message kept whole (price lists, catalogues); a longer one keeps its beginning and its end. 120 to 1000, default 400.</param>
/// <param name="TokenBudget">Estimated tokens of the conversation block; beyond it the oldest middle lines are left out. 300 to 50,000, default 4,000.</param>
/// <param name="KeepLastLines">Latest lines always kept, whatever the budget. 1 to 50, default 6.</param>
public sealed record ConversationPreprocessorOptions(int AdvisorMaxCharacters, int TokenBudget, int KeepLastLines)
{
    public const string SectionName = "Classification:Llm:Preprocessing";
    public const string AdvisorMaxCharactersKey = SectionName + ":AdvisorMaxCharacters";
    public const string TokenBudgetKey = SectionName + ":TokenBudget";
    public const string KeepLastLinesKey = SectionName + ":KeepLastLines";

    public const int MinAdvisorMaxCharacters = 120;
    public const int MaxAdvisorMaxCharacters = 1000;
    public const int MinTokenBudget = 300;
    public const int MaxTokenBudget = 50_000;
    public const int MinKeepLastLines = 1;
    public const int MaxKeepLastLines = 50;

    public static ConversationPreprocessorOptions Default { get; } = new(AdvisorMaxCharacters: 400, TokenBudget: 4000, KeepLastLines: 6);

    /// <summary>The preprocessor version and every setting, as one string: an input of the snapshot hash.</summary>
    public string Fingerprint => $"pre{ConversationPreprocessor.Version}:adv{AdvisorMaxCharacters}:tok{TokenBudget}:last{KeepLastLines}";

    /// <summary>Throws <see cref="InvalidOperationException"/> naming the first key out of range (and the range), never a secret.</summary>
    public void Validate()
    {
        static void Range(string key, int value, int min, int max)
        {
            if (value < min || value > max)
            {
                throw new InvalidOperationException($"{key} must be between {min} and {max} (it is {value}).");
            }
        }

        Range(AdvisorMaxCharactersKey, AdvisorMaxCharacters, MinAdvisorMaxCharacters, MaxAdvisorMaxCharacters);
        Range(TokenBudgetKey, TokenBudget, MinTokenBudget, MaxTokenBudget);
        Range(KeepLastLinesKey, KeepLastLines, MinKeepLastLines, MaxKeepLastLines);
    }
}
