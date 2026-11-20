using Conversia.Application.Assistant.Llm;
using Conversia.Domain.Entities;
using static Conversia.Application.UnitTests.Assistant.Llm.AssistantFixtures;

namespace Conversia.Application.UnitTests.Assistant.Llm;

/// <summary>Review round of the guard: when it may be skipped (item 4), its thresholds (7) and its bounded input (8).</summary>
public class AssistantGuardReviewTests
{
    [Theory]
    [InlineData("¿cuánto cuesta axilas?")]
    [InlineData("precio de piernas")]
    [InlineData("horario")]
    [InlineData("¿Qué horario tienen?")]
    [InlineData("Hola, ¿cuánto vale bikini?")]
    [InlineData("?")]
    [InlineData("👍")]
    [InlineData("🙂🙂")]
    public void OnlyCourtesyOrACatalogQuestion_Skips(string text)
        => Assert.True(AssistantGuard.CanSkip([text], Laser()));

    [Theory]
    [InlineData("Hola, soy Laura. ¿Cuánto vale axilas?")]
    [InlineData("no me atendieron bien, precio axilas")]
    [InlineData("me quemó la piel, cuánto vale axilas")]
    [InlineData("mal servicio, cuánto vale axilas")]
    [InlineData("precio axilas y de paso cuéntame un chiste")]
    [InlineData("¿cuánto vale axilas? quiero hacer un reclamo")]
    public void AnyOtherContentWord_OrAComplaintCue_GoesThroughTheGuard(string text)
        => Assert.False(AssistantGuard.CanSkip([text], Laser()));

    [Theory]
    [InlineData(GuardLabel.WantsHuman, 0.45, "Hola", GuardRouteKind.Handoff, "human_requested")]
    [InlineData(GuardLabel.Complaint, 0.4, "Hola", GuardRouteKind.Handoff, "complaint")]
    [InlineData(GuardLabel.Complaint, 0.3, "¿cuánto vale axilas?", GuardRouteKind.Proceed, null)]
    [InlineData(GuardLabel.Complaint, 0.3, "la sesión pasada me quemó la piel", GuardRouteKind.Handoff, "complaint")]
    [InlineData(GuardLabel.WantsHuman, 0.2, "¿me puede escribir un asesor?", GuardRouteKind.Handoff, "human_requested")]
    [InlineData(GuardLabel.WantsHuman, 0.2, "¿cuánto vale axilas?", GuardRouteKind.Proceed, null)]
    public void PersonAndComplaint_HandOffEarly_AndACueBelowTheThreshold_StillHandsOff(GuardLabel label, double confidence, string text, GuardRouteKind kind, string? reason)
    {
        var route = AssistantGuard.Route(new GuardVerdict(label, confidence), 0, [text]);
        Assert.Equal((kind, reason), (route.Kind, route.HandoffReason));
    }

    [Fact]
    public void TheHandoffThreshold_IsConfigurable()
        => Assert.Equal(GuardRouteKind.Proceed, AssistantGuard.Route(new GuardVerdict(GuardLabel.WantsHuman, 0.5), 0, ["Hola"], handoffConfidence: 0.7).Kind);

    [Fact]
    public void TheGuardInput_IsTheLastTurn_Bounded_KeepingItsEnd()
    {
        var turn = Enumerable.Range(1, 10).Select(i => $"mensaje {i} " + new string('x', 400)).Append("FINAL del turno").ToList();

        var request = AssistantGuard.Build(turn, new string('y', 2000), Laser());

        var start = request.UserContent.IndexOf("<customer_message>", StringComparison.Ordinal);
        var block = request.UserContent[start..];
        Assert.True(block.Length <= AssistantGuard.MaxInputCharacters + 100, $"the customer block is {block.Length} characters");
        Assert.Contains("FINAL del turno", block, StringComparison.Ordinal);
        Assert.True(request.UserContent.Length <= (AssistantGuard.MaxInputCharacters * 2) + 1500);
    }
}

/// <summary>Item 16: both cached prefixes reach Haiku 5.5's 512-token minimum with a margin, counted conservatively (4 characters per token).</summary>
public class CachedPrefixSizeTests
{
    private const int MinimumTokens = 512;
    private const double Margin = 1.5;

    [Fact]
    public void TheReplyPrefix_TheSystemPromptAlone_IsCacheable()
        => Assert.True(AssistantPromptBuilder.SystemPrompt.Length / 4.0 >= MinimumTokens * Margin, $"{AssistantPromptBuilder.SystemPrompt.Length} characters");

    [Fact]
    public void TheGuardPrefix_IsCacheable()
        => Assert.True((AssistantGuard.SystemPrompt.Length + AssistantGuard.ContextBlock.Length) / 4.0 >= MinimumTokens * Margin,
            $"{AssistantGuard.SystemPrompt.Length + AssistantGuard.ContextBlock.Length} characters");
}
