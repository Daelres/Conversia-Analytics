using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Conversia.Application.Classification.Llm;

/// <param name="SystemPrompt">The static instructions (embedded <c>system-prompt.es-CO.md</c>): the first cacheable block.</param>
/// <param name="ContextBlock">The catalog and whether the age range is asked: static per deployment, the second cacheable block.</param>
/// <param name="UserContent">The conversation block: <c>&lt;conversation&gt;</c>, the compact lines, <c>&lt;/conversation&gt;</c>.</param>
/// <param name="PromptVersion">Names the prompt, the context and the preprocessor; an input of the snapshot hash.</param>
/// <param name="LastLine">The highest message number; what the mapper accepts as a cited line.</param>
/// <param name="Schema">The JSON schema of the structured answer.</param>
/// <param name="PresentLines">The message numbers shown in the block: what <see cref="LlmResultMapper.Map"/> accepts as a cited line.</param>
public sealed record LlmPrompt(string SystemPrompt, string ContextBlock, string UserContent, string PromptVersion, int LastLine, JsonObject Schema, IReadOnlySet<int> PresentLines);

/// <summary>
/// Builds the request the model reads from a <see cref="PreprocessedConversation"/> (already pseudonymized and compacted). The prefix (system
/// prompt and context block) never contains a date, an id, a name or anything of a tenant, so it is identical for every conversation of a
/// deployment and can be cached by the provider; only the conversation block changes. Message bodies are neutralized again here
/// (<see cref="PromptTextNeutralizer"/>, idempotent) so the builder is safe on its own.
/// </summary>
public sealed class LlmPromptBuilder
{
    /// <summary>The revision of the embedded system prompt. Bump it with any edit of the file (the version also hashes the text).</summary>
    public const string SystemPromptRevision = "es-CO.1";

    private const string ResourceName = "Conversia.Application.Classification.Llm.system-prompt.es-CO.md";

    private static readonly Lazy<string> SystemPromptText = new(ReadSystemPrompt);

    private readonly ExtractionSchema _schema;
    private readonly string _contextBlock;

    public LlmPromptBuilder(ExtractionSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        _schema = schema;
        _contextBlock = BuildContextBlock(schema);
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(SystemPromptText.Value + "\n" + _contextBlock)));
        PromptVersion = $"{SystemPromptRevision}.pre{ConversationPreprocessor.Version}.{digest[..12]}";
    }

    /// <summary>Changes with the system prompt, the catalog, the age switch and the preprocessor version.</summary>
    public string PromptVersion { get; }

    /// <summary>The embedded system prompt, as shipped.</summary>
    public static string LoadSystemPrompt() => SystemPromptText.Value;

    public LlmPrompt Build(PreprocessedConversation conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        var entries = conversation.Entries.Select(e => e is PromptLine line
            ? line with { Segments = [.. line.Segments.Select(s => s with { Text = NeutralizeSegment(s.Text) })] }
            : e);
        var user = "<conversation>\n" + PromptFormat.RenderLines(entries) + "\n</conversation>";
        return new LlmPrompt(SystemPromptText.Value, _contextBlock, user, PromptVersion, conversation.LastLine, _schema.ToJson(), conversation.PresentNumbers);
    }

    // A back-reference the preprocessor wrote is kept; any other body is neutralized (a no-op on text the preprocessor already neutralized).
    private static string NeutralizeSegment(string text)
        => text.Length > 1 && text[0] == '=' && text[1..].All(char.IsAsciiDigit) ? text : PromptTextNeutralizer.Neutralize(text);

    private static string BuildContextBlock(ExtractionSchema schema)
    {
        var text = new StringBuilder();
        text.Append("# Catalog\n\n");
        text.Append("services: ").Append(string.Join(", ", schema.Catalog.Services)).Append('\n');
        text.Append("interest_reason: ").Append(string.Join(", ", schema.Catalog.InterestReasons)).Append('\n');
        text.Append(schema.ExtractAgeRange
            ? "age_range: requested (only when the customer states it; see the rules)\n"
            : "age_range: not requested (the schema has no age field; never estimate an age)\n");
        return text.ToString();
    }

    private static string ReadSystemPrompt()
    {
        using var stream = typeof(LlmPromptBuilder).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The LLM system prompt is missing from the assembly.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
    }
}
