using System.Text.Json.Nodes;
using Conversia.Application.Classification;
using Conversia.Application.Classification.Llm;
using Conversia.Domain.Enums;

namespace Conversia.Application.UnitTests.Classification.Llm;

/// <summary>
/// The LLM provider (design D2): pseudonymizer, preprocessor, prompt builder, client and strict mapper, composed. The client is a fake; the
/// answer is checked the way the coordinator relies on: a usable reading, or a content failure kind, never an exception for a bad answer.
/// </summary>
public class LlmConversationClassifierTests
{
    internal static readonly ClassificationRuleSet Rules = ClassificationRuleSet.LoadDefault();
    internal static readonly LlmCatalog Catalog = LlmCatalog.FromRules(Rules);

    internal sealed class FakeLlmClient(Func<LlmRequest, LlmCompletion> answer) : ILlmClient
    {
        public List<LlmRequest> Requests { get; } = [];

        public Task<LlmCompletion> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(answer(request));
        }
    }

    internal static string Answer(
        string stage = "in_follow_up", string? loss = null, string objection = "none", string[]? services = null, string? reason = null,
        string gender = "undetermined", decimal confidence = 0.8m, int? wonLine = null, bool includeAge = false, string? age = null)
    {
        var json = new JsonObject
        {
            ["stage"] = stage,
            ["loss_reason"] = loss,
            ["objection"] = objection,
            ["services"] = new JsonArray([.. (services ?? []).Select(s => (JsonNode?)JsonValue.Create(s))]),
            ["interest_reason"] = reason,
            ["gender"] = gender,
            ["confidence"] = confidence,
            ["won_evidence_line"] = wonLine,
        };
        if (includeAge)
        {
            json["age_range"] = age;
        }

        return json.ToJsonString();
    }

    internal static LlmCompletion Completed(string? json, LlmStopReason stop = LlmStopReason.EndTurn)
        => new(stop, json, new LlmUsage(900, 60, 3000, 0), "claude-haiku-5-5");

    internal static LlmConversationClassifier New(
        ILlmClient client, bool extractAgeRange = false, string model = "claude-haiku-5-5", byte[]? hashKey = null, ClassificationRuleSet? rules = null,
        LlmCatalog? catalog = null)
    {
        var preprocessing = ConversationPreprocessorOptions.Default;
        rules ??= Rules;
        catalog ??= Catalog;
        return new LlmConversationClassifier(
            new ConversationPreprocessor(preprocessing, rules),
            new LlmPromptBuilder(new ExtractionSchema(catalog, extractAgeRange)),
            new LlmResultMapper(catalog, extractAgeRange),
            client,
            new LlmClassifierSettings(model, extractAgeRange, preprocessing.Fingerprint, rules.Fingerprint(), hashKey));
    }

    private static readonly ConversationSnapshot PricedChat = ChatScript.Snapshot("""
        C: Hola, soy Maria Perez, cuánto cuesta la depilación de axilas? mi cel 3001234567
        A: Son $60.000 la sesión.
        C: Muy caro, no gracias
        """) with { KnownNames = ["Maria Perez"], ContactName = "Maria Perez" };

    [Fact]
    public async Task TheRequest_CarriesTheStaticPrefix_ThePseudonymizedConversation_TheSchema_AndTheHash()
    {
        var client = new FakeLlmClient(_ => Completed(Answer()));
        var classifier = New(client);

        var prepared = classifier.Prepare(PricedChat);
        await classifier.CallAsync(prepared);

        var request = Assert.Single(client.Requests);
        Assert.Equal(LlmPromptBuilder.LoadSystemPrompt(), request.SystemPrompt);
        Assert.Contains("services: ", request.ContextBlock);
        Assert.StartsWith("<conversation>", request.UserContent);
        Assert.DoesNotContain("3001234567", request.UserContent);
        Assert.DoesNotContain("Maria", request.UserContent);
        Assert.Contains("[CLIENTE]", request.UserContent);
        Assert.Equal("object", request.Schema["type"]!.GetValue<string>());
        Assert.Equal(prepared.SnapshotHash, request.SnapshotHash);
        Assert.Matches("^[0-9a-f]{64}$", prepared.SnapshotHash);
    }

    [Fact]
    public void TheHash_IsTheSnapshotHashOfTheRealSnapshot_WithTheModelAndThePromptVersion()
    {
        var classifier = New(new FakeLlmClient(_ => Completed(Answer())));
        var builder = new LlmPromptBuilder(new ExtractionSchema(Catalog, false));
        var expected = SnapshotHasher.Compute(PricedChat, new SnapshotHashInputs(
            builder.PromptVersion, "claude-haiku-5-5", ExtractionSchema.Version, false, ConversationPreprocessorOptions.Default.Fingerprint, Rules.Fingerprint()));

        Assert.Equal(expected, classifier.Prepare(PricedChat).SnapshotHash);
        Assert.NotEqual(expected, New(new FakeLlmClient(_ => Completed(Answer())), model: "claude-haiku-9").Prepare(PricedChat).SnapshotHash);
    }

    [Fact]
    public void EditingTheRulesFile_OrTheCatalog_ChangesTheHash()
    {
        // The rules feed the Won guard and the catalog feeds the schema: a stored hash must not survive an edit of either.
        var hash = New(new FakeLlmClient(_ => Completed(Answer()))).Prepare(PricedChat).SnapshotHash;
        var editedRules = ClassificationRuleSet.LoadDefault();
        editedRules.Withdrawal.Add("ya no quiero nada");
        var otherCatalog = Catalog with { InterestReasons = [.. Catalog.InterestReasons, "referido_amiga"] };

        Assert.NotEqual(hash, New(new FakeLlmClient(_ => Completed(Answer())), rules: editedRules).Prepare(PricedChat).SnapshotHash);
        Assert.NotEqual(hash, New(new FakeLlmClient(_ => Completed(Answer())), catalog: otherCatalog).Prepare(PricedChat).SnapshotHash);
        Assert.Equal(Rules.Fingerprint(), ClassificationRuleSet.LoadDefault().Fingerprint());
        Assert.Matches("^[0-9a-f]{64}$", Rules.Fingerprint());
    }

    [Fact]
    public void WithAHashKey_TheStoredHashIsAnHmacOfTheSnapshotHash()
    {
        var key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        var plain = New(new FakeLlmClient(_ => Completed(Answer()))).Prepare(PricedChat).SnapshotHash;

        var keyed = New(new FakeLlmClient(_ => Completed(Answer())), hashKey: key).Prepare(PricedChat).SnapshotHash;

        var expected = Convert.ToHexStringLower(System.Security.Cryptography.HMACSHA256.HashData(key, System.Text.Encoding.UTF8.GetBytes(plain)));
        Assert.Equal(expected, keyed);
        Assert.NotEqual(plain, keyed);
    }

    [Fact]
    public async Task AUsableAnswer_IsMapped_WithItsUsage()
    {
        var service = Catalog.Services[0];
        var classifier = New(new FakeLlmClient(_ => Completed(Answer("lost", "price", "price", [service], gender: "female", confidence: 0.9m))));

        var attempt = await classifier.CallAsync(classifier.Prepare(PricedChat));

        Assert.Null(attempt.Failure);
        Assert.NotNull(attempt.Result);
        Assert.Equal((ClassificationOutcome.Lost, (LossReason?)LossReason.Price, ObjectionType.Price, service, Gender.Female),
            (attempt.Result!.Outcome, attempt.Result.LossReason, attempt.Result.Objection, attempt.Result.ServiceInterest, attempt.Result.Gender));
        Assert.StartsWith("llm.", attempt.Result.RuleCodes[0]);
        Assert.Equal(new LlmUsage(900, 60, 3000, 0), attempt.Usage);
        Assert.Equal("claude-haiku-5-5", attempt.ModelId);
    }

    [Theory]
    [InlineData(LlmStopReason.Refusal, LlmFailureKind.Refused)]
    [InlineData(LlmStopReason.MaxTokens, LlmFailureKind.Truncated)]
    [InlineData(LlmStopReason.Other, LlmFailureKind.InvalidOutput)]
    public async Task AnAnswerThatDidNotEndNormally_IsAContentFailure_WithItsUsageKept(LlmStopReason stop, LlmFailureKind expected)
    {
        var classifier = New(new FakeLlmClient(_ => Completed(Answer(), stop)));

        var attempt = await classifier.CallAsync(classifier.Prepare(PricedChat));

        Assert.Equal((expected, (ClassificationResult?)null), (attempt.Failure, attempt.Result));
        Assert.Equal(900, attempt.Usage.InputTokens);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"stage\":\"won\"}")]
    [InlineData(null)]
    public async Task AnAnswerTheMapperRejects_IsInvalidOutput_WithTheMapperCodeOnly(string? json)
    {
        var classifier = New(new FakeLlmClient(_ => Completed(json)));

        var attempt = await classifier.CallAsync(classifier.Prepare(PricedChat));

        Assert.Equal(LlmFailureKind.InvalidOutput, attempt.Failure);
        Assert.NotNull(attempt.Error);
        Assert.DoesNotContain("won", attempt.Error!.Replace("won_evidence", string.Empty));
    }

    [Theory]
    [InlineData(LlmFailureKind.Timeout)]
    [InlineData(LlmFailureKind.RateLimited)]
    [InlineData(LlmFailureKind.Unavailable)]
    [InlineData(LlmFailureKind.InvalidOutput)]
    public async Task AFailedCall_ReportsItsKind_AndTheProviderErrorTypeOnly(LlmFailureKind kind)
    {
        var classifier = New(new FakeLlmClient(_ => throw new LlmCallException(kind, "SomeProviderException")));

        var attempt = await classifier.CallAsync(classifier.Prepare(PricedChat));

        Assert.Equal((kind, "SomeProviderException", LlmUsage.None), (attempt.Failure, attempt.Error, attempt.Usage));
    }

    [Theory]
    [InlineData("A: Hola, tenemos promoción de axilas\nA: ¿Te interesa?")]
    [InlineData("C: [audio]\nA: No pude escuchar el audio")]
    [InlineData("C: [image]")]
    public void AConversationWithoutCustomerText_IsFlaggedBeforeAnyCall(string script)
        => Assert.False(New(new FakeLlmClient(_ => Completed(Answer()))).Prepare(ChatScript.Snapshot(script)).HasCustomerText);

    [Fact]
    public void AConversationWithCustomerText_IsFlaggedAsSuch_WithThePreprocessorStatistics()
    {
        var prepared = New(new FakeLlmClient(_ => Completed(Answer()))).Prepare(PricedChat);

        Assert.True(prepared.HasCustomerText);
        Assert.Equal(3, prepared.Stats.MessagesIn);
        Assert.True(prepared.Stats.CharactersAfter > 0);
    }

    [Fact]
    public async Task TheAgeRange_IsReadOnlyWhenItIsAsked()
    {
        var classifier = New(new FakeLlmClient(_ => Completed(Answer(includeAge: true, age: "25_34"))), extractAgeRange: true);

        var attempt = await classifier.CallAsync(classifier.Prepare(PricedChat));

        Assert.Equal(AgeRange.From25To34, attempt.Result!.AgeRange);
    }
}
