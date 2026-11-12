using System.Text.Json.Nodes;
using Conversia.Application.Classification;
using Conversia.Application.Classification.Llm;
using Conversia.Domain.Enums;

namespace Conversia.Application.UnitTests.Classification.Llm;

/// <summary>The closed vocabulary between Conversia and the model: the catalog, the JSON schema and the strict mapper (spec CC-2, CC-3, CP-1, CP-3).</summary>
public class ExtractionContractTests
{
    private static readonly LlmCatalog Catalog = LlmCatalog.FromRules(ClassificationRuleSet.LoadDefault());

    // ---- Catalog (D7) ------------------------------------------------------------------------------------------------

    [Fact]
    public void TheCatalog_IsTheCodesOfTheRulesFile_PlusOther()
    {
        Assert.Equal(["axilas", "piernas", "bikini", "rostro", "bozo", "brazos", "espalda", "abdomen", "pecho", "gluteos", "cuerpo_completo", "depilacion_laser", "other"], Catalog.Services);
        Assert.Equal(["precio_promocion", "estetica", "comodidad", "salud", "evento", "recomendacion", "other"], Catalog.InterestReasons);
    }

    // ---- Schema ------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheSchema_IsClosed_AndCarriesTheCatalog()
    {
        var schema = new ExtractionSchema(Catalog, extractAgeRange: false).ToJson();

        Assert.False(schema["additionalProperties"]!.GetValue<bool>());
        var properties = schema["properties"]!.AsObject();
        Assert.Equal(["stage", "loss_reason", "objection", "services", "interest_reason", "gender", "confidence", "won_evidence_line"], properties.Select(p => p.Key));
        Assert.Equal(properties.Select(p => p.Key), schema["required"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Contains("other", properties["services"]!["items"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.DoesNotContain("no_response", properties["loss_reason"]!["enum"]!.AsArray().Select(n => n?.GetValue<string>()));
    }

    [Fact]
    public void TheAgeRange_IsAskedOnlyWhenEnabled()
    {
        // CP-5 S3: off by default, the schema carries no age field.
        Assert.Null(new ExtractionSchema(Catalog, extractAgeRange: false).ToJson()["properties"]!["age_range"]);

        var withAge = new ExtractionSchema(Catalog, extractAgeRange: true).ToJson();
        Assert.Equal(["under_18", "18_24", "25_34", "35_44", "45_54", "55_plus", null], withAge["properties"]!["age_range"]!["enum"]!.AsArray().Select(n => n?.GetValue<string>()));
        Assert.Contains("age_range", withAge["required"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    // ---- Mapper ------------------------------------------------------------------------------------------------------

    private static JsonObject Answer(Action<JsonObject>? change = null)
    {
        var json = new JsonObject
        {
            ["stage"] = "lost",
            ["loss_reason"] = "price",
            ["objection"] = "price",
            ["services"] = new JsonArray("piernas"),
            ["interest_reason"] = "estetica",
            ["gender"] = "female",
            ["confidence"] = 0.82,
            ["won_evidence_line"] = null,
        };
        change?.Invoke(json);
        return json;
    }

    private static readonly IReadOnlySet<int> TenLines = Enumerable.Range(1, 10).ToHashSet();

    private static LlmMapping Map(JsonObject json, bool extractAgeRange = false, IReadOnlySet<int>? lines = null)
        => new LlmResultMapper(Catalog, extractAgeRange).Map(json.ToJsonString(), lines ?? TenLines);

    [Fact]
    public void AValidAnswer_MapsToTheDomainVocabulary()
    {
        // CC-2 S1.
        var mapping = Map(Answer());

        Assert.True(mapping.Succeeded, mapping.Error);
        var r = mapping.Result!;
        Assert.Equal(("piernas", "estetica", ObjectionType.Price, ClassificationOutcome.Lost, (LossReason?)LossReason.Price, 0.82m, Gender.Female),
            (r.ServiceInterest, r.InterestReason, r.Objection, r.Outcome, r.LossReason, r.Confidence, r.Gender));
        Assert.All(r.RuleCodes, c => Assert.StartsWith("llm.", c));
        Assert.Equal(["llm.outcome.lost", "llm.loss.price", "llm.objection.price"], r.RuleCodes);
    }

    [Fact]
    public void NothingToRead_MapsToEmptyFields_AndUndetermined()
    {
        // CC-2 S2 and CC-4: "hola" gives no service, reason, objection or age and an undetermined gender; the stage is left alone.
        var mapping = Map(Answer(j =>
        {
            j["stage"] = "undetermined";
            j["loss_reason"] = null;
            j["objection"] = "none";
            j["services"] = new JsonArray();
            j["interest_reason"] = null;
            j["gender"] = "undetermined";
        }));

        var r = mapping.Result!;
        Assert.Equal(((string?)null, (string?)null, ObjectionType.None, ClassificationOutcome.Undetermined, (AgeRange?)null, Gender.Undetermined),
            (r.ServiceInterest, r.InterestReason, r.Objection, r.Outcome, r.AgeRange, r.Gender));
    }

    [Fact]
    public void AServiceOutsideTheCatalog_BecomesOther_AndIsNotAFailure()
    {
        // CC-3 S5.
        var r = Map(Answer(j => j["services"] = new JsonArray("depilacion_alien", "axilas", "otra_cosa"))).Result!;

        Assert.Equal("other,axilas", r.ServiceInterest);
        Assert.Contains("llm.service.other", r.RuleCodes);
    }

    [Fact]
    public void AnOmittedGender_IsUndetermined()
    {
        // CP-3 S3.
        Assert.Equal(Gender.Undetermined, Map(Answer(j => j.Remove("gender"))).Result!.Gender);
    }

    [Fact]
    public void TheAgeRange_IsMappedWhenAsked()
    {
        // CP-1 S1-S3: the model answers the coarse range; nothing else is kept.
        Assert.Equal(AgeRange.From25To34, Map(Answer(j => j["age_range"] = "25_34"), extractAgeRange: true).Result!.AgeRange);
        Assert.Equal("missing:age_range", Map(Answer(), extractAgeRange: true).Error); // the schema requires it when it is asked
        Assert.Equal(AgeRange.Under18, Map(Answer(j => j["age_range"] = "under_18"), extractAgeRange: true).Result!.AgeRange);
        Assert.Null(Map(Answer(j => j["age_range"] = null), extractAgeRange: true).Result!.AgeRange);
    }

    [Fact]
    public void TheWonEvidenceLine_IsKept_WhenItIsALineOfThePrompt()
    {
        Assert.Equal(3, Map(Answer(j => { j["stage"] = "won"; j["loss_reason"] = null; j["won_evidence_line"] = 3; })).Result!.WonEvidenceIndex);
    }

    [Fact]
    public void AnEvidenceLineNotShownInThePrompt_IsRejected()
    {
        // Message 2 was dropped (media) and 5 to 8 were left out by the budget: the model cannot have read them.
        var shown = new HashSet<int> { 1, 3, 4, 9, 10 };
        JsonObject Won(int line) => Answer(j => { j["stage"] = "won"; j["loss_reason"] = null; j["won_evidence_line"] = line; });

        Assert.Equal(9, Map(Won(9), lines: shown).Result!.WonEvidenceIndex);
        Assert.Equal("invalid:won_evidence_line", Map(Won(2), lines: shown).Error);
        Assert.Equal("invalid:won_evidence_line", Map(Won(6), lines: shown).Error);
    }

    [Fact]
    public void ALossWithoutAReason_IsLostWithNoReason()
    {
        var r = Map(Answer(j => j["loss_reason"] = null)).Result!;

        Assert.Equal((ClassificationOutcome.Lost, (LossReason?)null), (r.Outcome, r.LossReason));
        Assert.Equal(["llm.outcome.lost", "llm.objection.price"], r.RuleCodes);
    }

    [Fact]
    public void ADuplicatedProperty_IsRejected()
    {
        // Which value would win depends on the parser; an answer that says two things is not an answer.
        const string json = "{\"stage\":\"lost\",\"stage\":\"won\",\"loss_reason\":null,\"objection\":\"none\",\"services\":[],\"interest_reason\":null,\"gender\":\"undetermined\",\"confidence\":0.5,\"won_evidence_line\":null}";

        Assert.Equal("duplicate_property", new LlmResultMapper(Catalog, extractAgeRange: false).Map(json, TenLines).Error);
    }

    public static TheoryData<string, Action<JsonObject>> Rejections => new()
    {
        { "missing stage (CC-2 S3)", j => j.Remove("stage") },
        { "missing objection", j => j.Remove("objection") },
        { "missing confidence", j => j.Remove("confidence") },
        { "no_response (CC-3 S2)", j => j["loss_reason"] = "no_response" },
        { "confidence 1.4 (CC-3 S3)", j => j["confidence"] = 1.4 },
        { "negative confidence", j => j["confidence"] = -0.1 },
        { "confidence as text", j => j["confidence"] = "0.8" },
        { "upper-case code (CC-3 S3)", j => j["services"] = new JsonArray("Axilas") },
        { "code with a space", j => j["services"] = new JsonArray("axilas piernas") },
        { "150-character code", j => j["services"] = new JsonArray(new string('a', 150)) },
        { "too many services", j => j["services"] = new JsonArray("axilas", "piernas", "bikini", "rostro", "bozo", "brazos") },
        { "unknown property (CC-3 S4)", j => j["notes"] = "x" },
        { "unknown stage", j => j["stage"] = "new" },
        { "unknown objection", j => j["objection"] = "money" },
        { "reason outside the catalog", j => j["interest_reason"] = "curiosidad" },
        { "loss reason with a stage that is not lost", j => j["stage"] = "in_follow_up" },
        { "unknown gender", j => j["gender"] = "f" },
        { "age range not asked", j => j["age_range"] = "25_34" },
        { "evidence line beyond the prompt", j => j["won_evidence_line"] = 11 },
        { "evidence line zero", j => j["won_evidence_line"] = 0 },
        { "services not an array", j => j["services"] = "axilas" },
    };

    [Theory]
    [MemberData(nameof(Rejections))]
    public void AnythingOutsideTheVocabulary_IsAFailure_NeverAPartialResult(string name, Action<JsonObject> change)
    {
        var mapping = Map(Answer(change));

        Assert.False(mapping.Succeeded, name);
        Assert.Null(mapping.Result);
        Assert.Matches("^[a-z_]+(:[a-z_]+)?$", mapping.Error!); // a code, never the model's text
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"stage\":\"lost\"")]
    public void TextThatIsNotAJsonObject_IsAFailure(string text)
        => Assert.False(new LlmResultMapper(Catalog, extractAgeRange: false).Map(text, TenLines).Succeeded);

    [Fact]
    public void AnAgeRangeAnsweredAsAnInteger_IsRejected()
    {
        // CP-1 S4.
        Assert.False(Map(Answer(j => j["age_range"] = 29), extractAgeRange: true).Succeeded);
        Assert.False(Map(Answer(j => j["age_range"] = "20_29"), extractAgeRange: true).Succeeded);
    }
}
