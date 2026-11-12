using Conversia.Application.Classification;
using Conversia.Application.Classification.Llm;
using Conversia.Domain.Enums;
using Conversia.Infrastructure.Classification.Llm;

namespace Conversia.Infrastructure.UnitTests.Classification;

/// <summary>
/// A test that calls the REAL Anthropic API. Skipped unless <see cref="KeyVariable"/> holds an API key: the suite never spends money or
/// sends data by default (not even when <c>ANTHROPIC_API_KEY</c> happens to be set). Run it on purpose:
/// <c>CONVERSIA_REAL_LLM_API_KEY=... dotnet test --filter "Category=RealLlm"</c>.
/// </summary>
public sealed class RealLlmFactAttribute : FactAttribute
{
    public const string KeyVariable = "CONVERSIA_REAL_LLM_API_KEY";

    public RealLlmFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(KeyVariable)))
        {
            Skip = $"Calls the real Anthropic API: set {KeyVariable} to run it.";
        }
    }
}

public class RealAnthropicTests
{
    private static readonly ClassificationRuleSet Rules = ClassificationRuleSet.LoadDefault();
    private static readonly LlmCatalog Catalog = LlmCatalog.FromRules(Rules);

    /// <summary>Claude Haiku 5.5's minimum cacheable prefix (docs: 512 tokens).</summary>
    private const int MinimumCacheablePrefixTokens = 512;

    private static LlmPrompt StaticPrompt()
        => new LlmPromptBuilder(new ExtractionSchema(Catalog, extractAgeRange: false)).Build(
            new ConversationPreprocessor(ConversationPreprocessorOptions.Default, Rules).Process(
                new ConversationSnapshot(Guid.Empty, null, DateTime.UnixEpoch, null, null, [new SnapshotMessage(MessageDirection.Inbound, "text", "hola", DateTime.UnixEpoch)])));

    [Fact]
    public void Spike_S2_TheStaticPrefix_IsLongEnoughToBeCached()
    {
        // Conservative: four characters per token (Spanish text tokenizes denser than that), so the real count is higher.
        var prompt = StaticPrompt();
        var conservativeTokens = (prompt.SystemPrompt.Length + prompt.ContextBlock.Length) / 4;

        Assert.True(conservativeTokens >= MinimumCacheablePrefixTokens, $"the cacheable prefix is about {conservativeTokens} tokens");
    }

    [RealLlmFact]
    [Trait("Category", "RealLlm")]
    public async Task TheRealModel_ClassifiesASyntheticConversation_AndTheSecondCallReadsThePrefixFromTheCache()
    {
        // CC-10 S3/S4 [needs-real-llm]. Synthetic text only: no real name or number.
        var key = Environment.GetEnvironmentVariable(RealLlmFactAttribute.KeyVariable)!;
        var client = new AnthropicLlmClient(
            AnthropicLlmClient.CreateSdkClient(key, AnthropicLlmClient.OfficialBaseUrl, TimeSpan.FromSeconds(30), maxRetries: 0),
            new AnthropicRequestSettings("claude-haiku-5-5", MaxTokens: 2048, Effort: "low", DisableThinking: false));
        var classifier = new LlmConversationClassifier(
            new ConversationPreprocessor(ConversationPreprocessorOptions.Default, Rules),
            new LlmPromptBuilder(new ExtractionSchema(Catalog, false)),
            new LlmResultMapper(Catalog, false),
            client,
            new LlmClassifierSettings("claude-haiku-5-5", false, ConversationPreprocessorOptions.Default.Fingerprint));
        var start = new DateTime(2026, 1, 5, 15, 0, 0, DateTimeKind.Utc);
        var snapshot = new ConversationSnapshot(Guid.NewGuid(), "Sede Prueba", start, start, start.AddMinutes(1),
        [
            new SnapshotMessage(MessageDirection.Inbound, "text", "Hola, cuánto cuesta la depilación láser de axilas?", start, Guid.NewGuid()),
            new SnapshotMessage(MessageDirection.Outbound, "text", "Hola, la sesión de axilas está en $60.000.", start.AddMinutes(1), Guid.NewGuid()),
            new SnapshotMessage(MessageDirection.Inbound, "text", "Uy, muy caro para mí, no gracias", start.AddMinutes(2), Guid.NewGuid()),
        ]);

        var first = await classifier.CallAsync(classifier.Prepare(snapshot));
        var second = await classifier.CallAsync(classifier.Prepare(snapshot));

        Assert.True(first.Failure is null, $"first call failed: {first.Failure} {first.Error}");
        Assert.NotNull(first.Result);
        Assert.Equal(ObjectionType.Price, first.Result!.Objection);
        Assert.True(second.Failure is null, $"second call failed: {second.Failure} {second.Error}");
        Assert.True(second.Usage.CacheReadTokens > 0, $"no cache read on the second call (first wrote {first.Usage.CacheWriteTokens}, read {first.Usage.CacheReadTokens})");
    }
}
