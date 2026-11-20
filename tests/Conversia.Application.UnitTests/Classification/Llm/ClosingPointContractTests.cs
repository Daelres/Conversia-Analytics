using System.Text.Json.Nodes;
using Conversia.Application.Classification;
using Conversia.Application.Classification.Llm;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;

namespace Conversia.Application.UnitTests.Classification.Llm;

/// <summary>The closing point in the contract with the model (docs/CLASSIFICATION.md, "Closing point"): schema, strict mapper, prompt.</summary>
public class ClosingPointContractTests
{
    private static readonly LlmCatalog Catalog = LlmCatalog.FromRules(ClassificationRuleSet.LoadDefault());
    private static readonly IReadOnlySet<int> TenLines = Enumerable.Range(1, 10).ToHashSet();

    private static JsonObject Answer(Action<JsonObject>? change = null)
    {
        var json = new JsonObject
        {
            ["stage"] = "in_follow_up",
            ["loss_reason"] = null,
            ["objection"] = "none",
            ["services"] = new JsonArray("piernas"),
            ["interest_reason"] = null,
            ["gender"] = "undetermined",
            ["confidence"] = 0.7,
            ["won_evidence_line"] = null,
            ["reached_stage"] = "price_shared",
            ["moved_off_channel"] = "phone_call",
            ["off_channel_evidence_line"] = 4,
            ["closed_by"] = "unknown",
            ["closed_outside_system"] = true,
        };
        change?.Invoke(json);
        return json;
    }

    private static LlmMapping Map(JsonObject json, IReadOnlySet<int>? lines = null)
        => new LlmResultMapper(Catalog, extractAgeRange: false).Map(json.ToJsonString(), lines ?? TenLines);

    private static string[] Enum(JsonObject schema, string property)
        => [.. schema["properties"]![property]!["enum"]!.AsArray().Select(n => n?.GetValue<string>() ?? "null")];

    [Fact]
    public void TheSchema_AsksTheClosingPoint_WithClosedLists_AndNoReservedCloser()
    {
        var schema = new ExtractionSchema(Catalog, extractAgeRange: false).ToJson();

        Assert.Equal(["first_contact", "needs_identified", "price_shared", "objection_raised", "appointment_proposed", "appointment_booked", "payment_or_deposit", "null"],
            Enum(schema, "reached_stage"));
        Assert.Equal(["none", "phone_call", "in_person", "other_channel"], Enum(schema, "moved_off_channel"));
        Assert.Equal(["unknown", "advisor", "customer_self_service"], Enum(schema, "closed_by"));
        Assert.Equal("boolean", schema["properties"]!["closed_outside_system"]!["type"]!.GetValue<string>());
        var required = schema["required"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Contains("off_channel_evidence_line", required);
        Assert.Contains("closed_outside_system", required);
        Assert.Equal("2", ExtractionSchema.Version);
    }

    [Fact]
    public void AValidAnswer_MapsTheClosingPoint()
    {
        var mapping = Map(Answer());

        Assert.True(mapping.Succeeded, mapping.Error);
        Assert.Equal(new ClosingPointReading(ReachedStage.PriceShared, OffChannelMove.PhoneCall, 4, ClosedBy.Unknown, true), mapping.Result!.ClosingPoint);
    }

    [Fact]
    public void AnAnswerWithoutTheClosingPoint_LeavesItUnread()
    {
        // An answer of the first schema (a canned Replay answer): the stage is read, the closing point is left to the rules.
        var mapping = Map(Answer(j =>
        {
            foreach (var key in new[] { "reached_stage", "moved_off_channel", "off_channel_evidence_line", "closed_by", "closed_outside_system" })
            {
                j.Remove(key);
            }
        }));

        Assert.True(mapping.Succeeded, mapping.Error);
        Assert.Null(mapping.Result!.ClosingPoint);
    }

    [Fact]
    public void ANullStage_AndNoMove_AreNothingRead()
    {
        var mapping = Map(Answer(j =>
        {
            j["reached_stage"] = null;
            j["moved_off_channel"] = "none";
            j["off_channel_evidence_line"] = null;
            j["closed_outside_system"] = false;
        }));

        Assert.True(mapping.Succeeded, mapping.Error);
        Assert.Equal(ClosingPointReading.Empty, mapping.Result!.ClosingPoint);
    }

    public static TheoryData<string, Action<JsonObject>> Rejected => new()
    {
        { "invalid:reached_stage", j => j["reached_stage"] = "closed" },
        { "invalid:reached_stage", j => j["reached_stage"] = 3 },
        { "invalid:moved_off_channel", j => j["moved_off_channel"] = "carrier_pigeon" },
        { "invalid:closed_by", j => j["closed_by"] = "ai_assistant" }, // reserved for stage 5: never from the model
        { "invalid:closed_outside_system", j => j["closed_outside_system"] = "yes" },
        { "invalid:off_channel_evidence_line", j => j["off_channel_evidence_line"] = 11 }, // not shown in the prompt
        { "invalid:off_channel_evidence_line", j => j["off_channel_evidence_line"] = null }, // a move needs its line
        { "invalid:off_channel_evidence_line", j => { j["moved_off_channel"] = "none"; } }, // a line without a move
        { "invalid:off_channel_evidence_line", j => j["off_channel_evidence_line"] = "4" },
    };

    [Theory]
    [MemberData(nameof(Rejected))]
    public void AMalformedClosingPoint_RejectsTheWholeAnswer(string error, Action<JsonObject> change)
    {
        var mapping = Map(Answer(change));

        Assert.False(mapping.Succeeded);
        Assert.Equal(error, mapping.Error);
    }

    [Fact]
    public void ThePrompt_ExplainsEveryClosingField_AndItsVersionMoved()
    {
        var prompt = LlmPromptBuilder.LoadSystemPrompt();

        foreach (var field in new[] { "## reached_stage", "## moved_off_channel", "## off_channel_evidence_line", "## closed_by", "## closed_outside_system" })
        {
            Assert.Contains(field, prompt, StringComparison.Ordinal);
        }

        foreach (var code in new[] { "first_contact", "needs_identified", "price_shared", "objection_raised", "appointment_proposed", "appointment_booked", "payment_or_deposit", "phone_call", "in_person", "other_channel", "customer_self_service" })
        {
            Assert.Contains("`" + code + "`", prompt, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("ai_assistant", prompt, StringComparison.Ordinal);
        Assert.Equal("es-CO.2", LlmPromptBuilder.SystemPromptRevision);
    }
}
