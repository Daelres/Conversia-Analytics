using Conversia.Application.Assistant.Llm;
using Conversia.Application.Classification.Llm;
using Conversia.AssistantEval;
using Conversia.Infrastructure.Classification.Llm;
using Conversia.Infrastructure.UnitTests.Classification;

namespace Conversia.Infrastructure.UnitTests.Assistant;

/// <summary>
/// The assistant against the REAL Anthropic API (Claude Haiku 5.5), over a few synthetic cases of the evaluation set. Skipped unless
/// <c>CONVERSIA_REAL_LLM_API_KEY</c> is set: <c>dotnet test --filter "Category=RealLlm"</c>. It costs money (cents) and sends synthetic text only.
/// </summary>
public class RealAssistantTests
{
    [RealLlmFact]
    [Trait("Category", "RealLlm")]
    public async Task TheRealModel_AnswersGrounded_AndEveryInjectionEndsSafe()
    {
        var key = Environment.GetEnvironmentVariable(RealLlmFactAttribute.KeyVariable)!;
        var sdk = AnthropicLlmClient.CreateSdkClient(key, AnthropicLlmClient.OfficialBaseUrl, TimeSpan.FromSeconds(15), maxRetries: 0);
        var clients = new AssistantLlmClients(
            new AnthropicLlmClient(sdk, new AnthropicRequestSettings("claude-haiku-5-5", 1024, "low", DisableThinking: true)),
            new AnthropicLlmClient(sdk, new AnthropicRequestSettings("claude-haiku-5-5", 128, "low", DisableThinking: true)));
        var set = EvalSet.Load(Path.Combine(AppContext.BaseDirectory, "eval-set.v1.json"));
        var evaluator = new AssistantEvaluator(_ => clients, LlmPricing.Default);

        foreach (var id in new[] { "laser-price-axilas", "laser-objection-price", "offtopic-football", "inj-fake-system", "inj-discount-pressure", "retail-price" })
        {
            var result = await evaluator.RunAsync(set, set.Cases.Single(c => c.Id == id));
            Assert.True(result.Passed, $"{id}: {string.Join("; ", result.Failures)}");
        }
    }
}
