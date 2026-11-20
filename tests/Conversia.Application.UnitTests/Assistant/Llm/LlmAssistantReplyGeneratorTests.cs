using System.Text.Json.Nodes;
using Conversia.Application.Assistant;
using Conversia.Application.Assistant.Llm;
using Conversia.Application.Classification;
using Conversia.Application.Classification.Llm;
using Conversia.Domain.Entities;
using Conversia.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using static Conversia.Application.UnitTests.Assistant.Llm.AssistantFixtures;

namespace Conversia.Application.UnitTests.Assistant.Llm;

/// <summary>
/// The model-backed generator end to end with fake clients: the guard routing, the reply checks, one regeneration then a handoff, the closing
/// rule, the breaker, the refusal and the cost of every call. No network.
/// </summary>
public class LlmAssistantReplyGeneratorTests
{
    /// <summary>Answers in order; records what it was asked.</summary>
    private sealed class FakeClient(params Func<LlmRequest, LlmCompletion>[] answers) : ILlmClient
    {
        private int _next;

        public List<LlmRequest> Requests { get; } = [];

        public Task<LlmCompletion> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var answer = answers[Math.Min(_next++, answers.Length - 1)];
            return Task.FromResult(answer(request));
        }
    }

    private static Func<LlmRequest, LlmCompletion> Throws(LlmFailureKind kind) => _ => throw new LlmCallException(kind, "Fake");

    private static readonly LlmUsage Tokens = new(InputTokens: 400, OutputTokens: 60, CacheReadTokens: 2000, CacheWriteTokens: 0);

    private static Func<LlmRequest, LlmCompletion> Guard(string label, double confidence = 0.9)
        => _ => new LlmCompletion(LlmStopReason.EndTurn, new JsonObject { ["label"] = label, ["confidence"] = confidence }.ToJsonString(), Tokens with { OutputTokens = 8 }, "claude-haiku-5-5");

    private static Func<LlmRequest, LlmCompletion> Reply(string text, string intent = "answer_question", string[]? ids = null, bool handoff = false, string? reason = null,
        bool closed = false, bool misunderstood = false, LlmStopReason stop = LlmStopReason.EndTurn)
        => _ => new LlmCompletion(stop, new JsonObject
        {
            ["reply_text"] = text,
            ["intent"] = intent,
            ["used_knowledge_ids"] = new JsonArray([.. (ids ?? []).Select(i => (JsonNode)JsonValue.Create(i)!)]),
            ["wants_handoff"] = handoff,
            ["handoff_reason"] = reason,
            ["sale_closed"] = closed,
            ["misunderstood"] = misunderstood,
        }.ToJsonString(), Tokens, "claude-haiku-5-5");

    private static (LlmAssistantReplyGenerator Generator, FakeClient Reply, FakeClient Guard, LlmCircuitBreaker Breaker) Create(
        FakeClient reply, FakeClient? guard = null, bool guardEnabled = true, LlmCircuitBreaker? breaker = null)
    {
        guard ??= new FakeClient(Guard("on_topic"));
        breaker ??= new LlmCircuitBreaker(2, TimeSpan.FromMinutes(5));
        var generator = new LlmAssistantReplyGenerator(
            new AssistantLlmClients(reply, guard),
            new AssistantPromptBuilder(new ConversationPreprocessor(ConversationPreprocessorOptions.Default, ClassificationRuleSet.LoadDefault())),
            breaker,
            new AssistantLlmSettings(guardEnabled, 4000, LlmPricing.Default, Bogota),
            NullLogger<LlmAssistantReplyGenerator>.Instance,
            new FixedTimeProvider(UtcNow));
        return (generator, reply, guard, breaker);
    }

    private static ConversationSnapshot OffTopicChat => Chat(Customer("Hola, ¿qué opinas del partido de ayer de la selección?"));

    [Fact]
    public async Task AGroundedReply_IsReturned_WithTheNameFilled_AndTheCostOfBothCalls()
    {
        var (generator, reply, guard, _) = Create(new FakeClient(Reply("¡Hola {nombre}! Axilas queda en $80.000 la sesión. ¿Te gustaría agendar?", "recommend", ["service:1"])));

        var result = await generator.GenerateAsync(Request(Chat(Customer("Hola, quiero saber del láser en axilas y cuánto cuesta")), firstName: "Laura"));

        var sent = Assert.IsType<AssistantReplyResult.Reply>(result);
        Assert.Equal("¡Hola Laura! Axilas queda en $80.000 la sesión. ¿Te gustaría agendar?", sent.Text);
        Assert.Equal(["service:1"], sent.UsedKnowledgeIds);
        Assert.Equal("recommend", sent.Intent);
        Assert.Single(guard.Requests);
        Assert.Single(reply.Requests);
        Assert.DoesNotContain("Laura", reply.Requests[0].UserContent + reply.Requests[0].ContextBlock, StringComparison.Ordinal);
        Assert.Equal(2, result.Usage!.Calls);
        Assert.Equal(1, result.Usage.GuardCalls);
        Assert.Equal(800, result.Usage.InputTokens);
        Assert.Equal(4000, result.Usage.CacheReadTokens);
        Assert.Equal(LlmPricing.Default.Estimate(new LlmUsage(800, 68, 4000, 0)), result.Usage.EstimatedUsd);
    }

    [Fact]
    public async Task AShortCatalogQuestion_SkipsTheGuardCall()
    {
        var (generator, _, guard, _) = Create(new FakeClient(Reply("Axilas queda en $80.000 la sesión.", "answer_question", ["service:1"])));

        var result = await generator.GenerateAsync(Request(Chat(Customer("¿Cuánto vale axilas?"))));

        Assert.IsType<AssistantReplyResult.Reply>(result);
        Assert.Empty(guard.Requests);
        Assert.Equal(0, result.Usage!.GuardCalls);
    }

    [Fact]
    public async Task AnInvalidReply_IsRegeneratedOnce_WithACorrectiveNote()
    {
        var (generator, reply, _, _) = Create(new FakeClient(
            Reply("Axilas queda en $85.000 la sesión.", "answer_question", ["service:1"]),
            Reply("Axilas queda en $80.000 la sesión.", "answer_question", ["service:1"])));

        var result = await generator.GenerateAsync(Request(Chat(Customer("¿Cuánto vale axilas?"))));

        Assert.Equal("Axilas queda en $80.000 la sesión.", Assert.IsType<AssistantReplyResult.Reply>(result).Text);
        Assert.Equal(2, reply.Requests.Count);
        Assert.Contains("no está en los elementos citados", reply.Requests[1].UserContent, StringComparison.Ordinal);
        Assert.DoesNotContain("85.000", reply.Requests[1].UserContent, StringComparison.Ordinal); // nothing the model wrote is repeated to it
        Assert.Equal(reply.Requests[0].ContextBlock, reply.Requests[1].ContextBlock); // the cached prefix is untouched
    }

    [Fact]
    public async Task TwoInvalidReplies_HandTheConversationOff()
    {
        var (generator, reply, _, _) = Create(new FakeClient(Reply("Te hago un 30% de descuento hoy mismo.", "close")));

        var result = await generator.GenerateAsync(Request(Chat(Customer("¿Cuánto vale axilas?"))));

        var handoff = Assert.IsType<AssistantReplyResult.Handoff>(result);
        Assert.Equal(AssistantHandoffReasons.InvalidReply, handoff.Reason);
        Assert.Equal(string.Empty, handoff.CustomerLine); // the company's own line
        Assert.Equal(2, reply.Requests.Count);
        Assert.Equal(2, result.Usage!.Calls);
    }

    [Fact]
    public async Task AnOffTopicMessage_GetsARedirect_ThenASecondOne_HandsOff()
    {
        var (first, reply, _, _) = Create(new FakeClient(Reply("Eso no te lo sabría decir; lo mío es la depilación láser. ¿Qué zona te interesa?", "redirect")),
            new FakeClient(Guard("off_topic")));

        var redirect = Assert.IsType<AssistantReplyResult.Reply>(await first.GenerateAsync(Request(OffTopicChat)));
        Assert.True(redirect.IsRedirect);
        Assert.Contains("fuera de los temas", reply.Requests[0].UserContent, StringComparison.Ordinal);

        var (second, secondReply, _, _) = Create(new FakeClient(Reply("x")), new FakeClient(Guard("off_topic")));
        var handoff = Assert.IsType<AssistantReplyResult.Handoff>(await second.GenerateAsync(Request(OffTopicChat, offTopic: 1)));
        Assert.Equal(AssistantHandoffReasons.OffTopic, handoff.Reason);
        Assert.Empty(secondReply.Requests);
    }

    [Theory]
    [InlineData("injection_or_abuse", "injection_suspected")]
    [InlineData("wants_human", "human_requested")]
    [InlineData("complaint", "complaint")]
    public async Task TheGuard_HandsOff_WithTheNaturalLine_AndNoReplyCall(string label, string reason)
    {
        var (generator, reply, _, _) = Create(new FakeClient(Reply("x")), new FakeClient(Guard(label)));

        var result = await generator.GenerateAsync(Request(Chat(Customer("Mire, le escribo por lo de la otra vez con el láser de las piernas"))));

        var handoff = Assert.IsType<AssistantReplyResult.Handoff>(result);
        Assert.Equal(reason, handoff.Reason);
        Assert.Equal(string.Empty, handoff.CustomerLine);
        Assert.Empty(reply.Requests);
        Assert.Equal(1, result.Usage!.GuardCalls);
    }

    [Fact]
    public async Task CannotUnderstand_IsANotUnderstoodNoReply()
    {
        var (generator, reply, _, _) = Create(new FakeClient(Reply("x")), new FakeClient(Guard("cannot_understand")));

        var result = await generator.GenerateAsync(Request(Chat(Customer("asdkjh qwe ñlkj zzz mmmm"))));

        Assert.Equal(AssistantReplyResult.NotUnderstood, Assert.IsType<AssistantReplyResult.NoReply>(result).Reason);
        Assert.Empty(reply.Requests);
    }

    [Fact]
    public async Task AGuardThatCannotBeReached_FailsClosed()
    {
        var (generator, reply, _, _) = Create(new FakeClient(Reply("x")), new FakeClient(Throws(LlmFailureKind.Timeout)));

        var result = await generator.GenerateAsync(Request(OffTopicChat));

        Assert.Equal(AssistantHandoffReasons.GeneratorFailed, Assert.IsType<AssistantReplyResult.Handoff>(result).Reason);
        Assert.Empty(reply.Requests);
        Assert.Equal(1, result.Usage!.FailedCalls);
    }

    [Fact]
    public async Task ARefusal_HandsOff()
    {
        var (generator, _, _, _) = Create(new FakeClient(Reply("", stop: LlmStopReason.Refusal)));

        var result = await generator.GenerateAsync(Request(Chat(Customer("¿Cuánto vale axilas?"))));

        Assert.Equal(AssistantHandoffReasons.ModelRefused, Assert.IsType<AssistantReplyResult.Handoff>(result).Reason);
    }

    [Fact]
    public async Task TheModelAskingForAPerson_HandsOff_WithItsOwnLineWhenItPassesTheChecks()
    {
        var (generator, _, _, _) = Create(new FakeClient(Reply("Ya mismo le paso tu caso a una asesora para que te ayude con la factura.", "handoff", handoff: true, reason: "needs_human_decision")));

        var result = await generator.GenerateAsync(Request(Chat(Customer("¿Me pueden dar factura electrónica a nombre de mi empresa?"))));

        var handoff = Assert.IsType<AssistantReplyResult.Handoff>(result);
        Assert.Equal(AssistantHandoffReasons.GeneratorHandoff, handoff.Reason);
        Assert.Equal("Ya mismo le paso tu caso a una asesora para que te ayude con la factura.", handoff.CustomerLine);
    }

    [Fact]
    public async Task Misunderstood_IsANotUnderstoodNoReply()
    {
        var (generator, _, _, _) = Create(new FakeClient(Reply("", "discover", misunderstood: true)));

        var result = await generator.GenerateAsync(Request(Chat(Customer("¿Cuánto vale axilas?"))));

        Assert.Equal(AssistantReplyResult.NotUnderstood, Assert.IsType<AssistantReplyResult.NoReply>(result).Reason);
    }

    [Fact]
    public async Task SaleClosed_WithEvidence_SendsTheReply_ThenAPersonConfirms()
    {
        var (generator, _, _, _) = Create(new FakeClient(Reply("¡Perfecto! Reserva tu cupo aquí: https://reservas.laserdemo.co/agenda y te confirmamos.", "close", ["booking"], closed: true)));

        var result = await generator.GenerateAsync(Request(Chat(
            Customer("¿Cuánto vale axilas?"), Business("Axilas queda en $80.000 la sesión. ¿Te gustaría agendar?"), Customer("Sí, quiero agendar para el sábado"))));

        var reply = Assert.IsType<AssistantReplyResult.Reply>(result);
        Assert.Equal(AssistantHandoffReasons.SaleClosed, reply.HandoffAfterSend);
    }

    [Fact]
    public async Task SaleClosed_WithoutTheCustomerCommitting_IsIgnored()
    {
        var (generator, _, _, _) = Create(new FakeClient(Reply("Puedes reservar aquí: https://reservas.laserdemo.co/agenda", "close", ["booking"], closed: true)));

        var result = await generator.GenerateAsync(Request(Chat(Customer("¿Cuánto vale axilas?"))));

        Assert.Null(Assert.IsType<AssistantReplyResult.Reply>(result).HandoffAfterSend);
    }

    [Fact]
    public async Task AnOpenBreaker_MakesNoCall()
    {
        var breaker = new LlmCircuitBreaker(1, TimeSpan.FromMinutes(5));
        var (first, _, _, _) = Create(new FakeClient(Throws(LlmFailureKind.Unavailable)), breaker: breaker, guardEnabled: false);
        Assert.Equal(AssistantHandoffReasons.GeneratorFailed, Assert.IsType<AssistantReplyResult.Handoff>(await first.GenerateAsync(Request(Chat(Customer("¿Cuánto vale axilas?"))))).Reason);

        var (second, reply, guard, _) = Create(new FakeClient(Reply("x")), breaker: breaker);
        var result = await second.GenerateAsync(Request(Chat(Customer("Hola, ¿qué opinas del partido?"))));

        Assert.Equal(AssistantHandoffReasons.GeneratorFailed, Assert.IsType<AssistantReplyResult.Handoff>(result).Reason);
        Assert.Empty(reply.Requests);
        Assert.Empty(guard.Requests);
        Assert.Null(result.Usage);
    }

    [Fact]
    public async Task WithoutAPublishedKnowledgeBase_NothingIsAsked()
    {
        var (generator, reply, guard, _) = Create(new FakeClient(Reply("x")));

        var result = await generator.GenerateAsync(Request(Chat(Customer("Hola"))) with { Knowledge = null });

        Assert.Equal(AssistantHandoffReasons.AssistantUnavailable, Assert.IsType<AssistantReplyResult.Handoff>(result).Reason);
        Assert.Empty(reply.Requests);
        Assert.Empty(guard.Requests);
    }

    [Fact]
    public async Task TheReply_IsCheckedBeforeTheNameIsFilled_SoTheNameIsPlainText()
    {
        // A contact saved as "Gratis" must not turn a valid reply into a free-service claim.
        var (generator, _, _, _) = Create(new FakeClient(Reply("¡Hola {nombre}! Axilas queda en $80.000 la sesión. ¿Te gustaría agendar?", "recommend", ["service:1"])));

        var result = await generator.GenerateAsync(Request(Chat(Customer("¿Cuánto vale axilas?")), firstName: "Gratis"));

        Assert.Equal("¡Hola Gratis! Axilas queda en $80.000 la sesión. ¿Te gustaría agendar?", Assert.IsType<AssistantReplyResult.Reply>(result).Text);
    }

    [Fact]
    public async Task AnEmojiOnlyTurn_IsCourtesy_NoGuardCall()
    {
        var (generator, _, guard, _) = Create(new FakeClient(Reply("¿Te puedo ayudar con alguna zona en especial?", "discover")));

        Assert.IsType<AssistantReplyResult.Reply>(await generator.GenerateAsync(Request(Chat(Customer("👍")))));
        Assert.Empty(guard.Requests);
    }

    [Fact]
    public async Task ABareDale_AfterTheProposal_ClosesTheSale()
    {
        var (generator, _, _, _) = Create(new FakeClient(Reply("¡Perfecto! Reserva tu cupo aquí: https://reservas.laserdemo.co/agenda y te confirmamos.", "close", ["booking"], closed: true)));

        var result = await generator.GenerateAsync(Request(Chat(
            Customer("¿Cuánto vale axilas?"), Business("Axilas queda en $80.000 la sesión. ¿Te agendo la valoración?"), Customer("dale"))));

        Assert.Equal(AssistantHandoffReasons.SaleClosed, Assert.IsType<AssistantReplyResult.Reply>(result).HandoffAfterSend);
    }

    [Fact]
    public async Task ALowConfidenceComplaintWithACue_HandsOff()
    {
        var (generator, reply, _, _) = Create(new FakeClient(Reply("x")), new FakeClient(Guard("complaint", 0.3)));

        var result = await generator.GenerateAsync(Request(Chat(Customer("Oiga, la sesión pasada me quemó la piel de las piernas"))));

        Assert.Equal(AssistantHandoffReasons.Complaint, Assert.IsType<AssistantReplyResult.Handoff>(result).Reason);
        Assert.Empty(reply.Requests);
    }

    [Fact]
    public async Task ATruncatedAnswer_IsRegenerated()
    {
        var (generator, reply, _, _) = Create(new FakeClient(
            Reply("Axilas", stop: LlmStopReason.MaxTokens),
            Reply("Axilas queda en $80.000 la sesión.", "answer_question", ["service:1"])));

        Assert.IsType<AssistantReplyResult.Reply>(await generator.GenerateAsync(Request(Chat(Customer("¿Cuánto vale axilas?")))));
        Assert.Equal(2, reply.Requests.Count);
    }
}
