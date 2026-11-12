using System.Text.Json.Nodes;
using Conversia.Application.Classification;
using Conversia.Application.Classification.Llm;
using Conversia.Infrastructure.Classification.Llm;

namespace Conversia.Infrastructure.UnitTests.Classification;

/// <summary>The Development-only replay client (spec CC-11): canned answers keyed by snapshot hash, a deterministic answer otherwise, zero cost.</summary>
public class ReplayLlmClientTests
{
    private static readonly LlmCatalog Catalog = LlmCatalog.FromRules(ClassificationRuleSet.LoadDefault());
    private static readonly string HashA = new('a', 64);
    private static readonly string HashB = new('b', 64);

    private static LlmRequest Request(string hash) => new("system", "context", "<conversation>\n[#1][C] hola\n</conversation>", new JsonObject(), hash);

    private static string WriteFile(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"replay-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public async Task ACannedAnswer_IsReturnedForItsHash()
    {
        var answer = new JsonObject { ["stage"] = "lost", ["loss_reason"] = "price", ["objection"] = "price", ["confidence"] = 0.9 };
        var path = WriteFile(new JsonObject { ["responses"] = new JsonObject { [HashA] = answer } }.ToJsonString());
        try
        {
            var client = ReplayLlmClient.Load(path, extractAgeRange: false);

            var completion = await client.CompleteAsync(Request(HashA));

            Assert.Equal(LlmStopReason.EndTurn, completion.StopReason);
            Assert.Equal("lost", JsonNode.Parse(completion.JsonText!)!["stage"]!.GetValue<string>());
            Assert.Equal((LlmUsage.None, ReplayLlmClient.ModelId), (completion.Usage, completion.ModelId));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WithoutACannedAnswer_TheAnswerIsDeterministic_AndPassesTheStrictMapper(bool extractAgeRange)
    {
        var client = ReplayLlmClient.Load(null, extractAgeRange);

        var first = await client.CompleteAsync(Request(HashB));
        var second = await client.CompleteAsync(Request(HashB));

        Assert.Equal(first, second);
        var mapping = new LlmResultMapper(Catalog, extractAgeRange).Map(first.JsonText, new HashSet<int> { 1 });
        Assert.True(mapping.Succeeded, mapping.Error);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"responses\":[]}")]
    [InlineData("{\"responses\":{\"short\":{}}}")]
    public void AnUnreadableFile_FailsAtLoad_NamingTheOptionNotTheContent(string json)
    {
        var path = WriteFile(json);
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => ReplayLlmClient.Load(path, false));
            Assert.Contains("Classification:Llm:ReplayPath", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AMissingFile_FailsAtLoad()
        => Assert.Throws<InvalidOperationException>(() => ReplayLlmClient.Load(Path.Combine(Path.GetTempPath(), "nope", "replay.json"), false));
}
