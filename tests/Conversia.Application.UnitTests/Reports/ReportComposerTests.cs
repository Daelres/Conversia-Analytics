using System.Text.Json.Nodes;
using Conversia.Application.Classification.Llm;
using Conversia.Application.Reports;
using Conversia.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conversia.Application.UnitTests.Reports;

/// <summary>Who writes a report, and every way of falling back to the template (docs/REPORTS.md, "Fallback").</summary>
public class ReportComposerTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly ReportDataPack Pack = ReportPacks.Sample();
    private static readonly LlmUsage Usage = new(1000, 500, 2000, 0);

    private sealed class FakeAdmission(ReportAiDecision decision) : IReportAiAdmission
    {
        public int Calls { get; private set; }

        public Task<ReportAiDecision> DecideAsync(Guid tenantId, bool sandboxMode, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(decision);
        }
    }

    private sealed class ScriptedClient(params Func<LlmCompletion>[] answers) : ILlmClient
    {
        public List<LlmRequest> Requests { get; } = [];

        public Task<LlmCompletion> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(answers[Math.Min(Requests.Count - 1, answers.Length - 1)]());
        }
    }

    private static string Answer(string diagnosis = "La conversión bajó 4,5 puntos y la sucursal Norte quedó en 14,3 %.", params string[] names)
        => new JsonObject
        {
            ["title"] = "Resumen semanal · semana del 9 al 15 de noviembre de 2026",
            ["indicators"] = new JsonArray(new JsonObject { ["label"] = "Conversión", ["value"] = "23,5 %", ["note"] = "Bajó frente a 28 %." }),
            ["diagnosis"] = diagnosis,
            ["actions"] = new JsonArray("Acompañar a la sucursal Norte esta semana."),
            ["coverage_note"] = null,
            ["mentioned_names"] = new JsonArray([.. (names.Length == 0 ? ["Norte"] : names).Select(n => (JsonNode)n)]),
        }.ToJsonString();

    private static Func<LlmCompletion> Says(string json) => () => new LlmCompletion(LlmStopReason.EndTurn, json, Usage, "claude-haiku-5-5");

    private static Func<LlmCompletion> Throws(LlmFailureKind kind) => () => throw new LlmCallException(kind, "Test");

    private static ReportComposer Composer(ILlmClient? client, IReportAiAdmission admission)
        => new(client, admission, LlmPricing.Default, NullLogger<ReportComposer>.Instance);

    [Fact]
    public async Task A_grounded_answer_is_used_with_its_model_tokens_and_cost()
    {
        var client = new ScriptedClient(Says(Answer()));

        var result = await Composer(client, new FakeAdmission(ReportAiDecision.Allowed)).ComposeAsync(Tenant, false, Pack, []);

        Assert.Equal((ReportGenerator.Llm, null, 1, "claude-haiku-5-5"), (result.Generator, result.FallbackReason, result.LlmAttempts, result.Model));
        Assert.Equal(Usage, result.Usage);
        Assert.Equal(LlmPricing.Default.Estimate(Usage), result.EstimatedUsd);
        Assert.Contains("Norte", result.Content.Diagnosis);
        var request = Assert.Single(client.Requests);
        Assert.Contains(Pack.ToJson(), request.UserContent);
        Assert.DoesNotContain("Láser Piloto", request.SystemPrompt + request.ContextBlock);
    }

    [Fact]
    public async Task Without_a_configured_model_the_template_writes_it()
    {
        var admission = new FakeAdmission(ReportAiDecision.Allowed);

        var result = await Composer(null, admission).ComposeAsync(Tenant, false, Pack, []);

        Assert.Equal((ReportGenerator.Template, ReportFallbackReasons.NotConfigured, 0), (result.Generator, result.FallbackReason, result.LlmAttempts));
        Assert.Equal(TemplateReportWriter.Write(Pack), result.Content, ContentComparer.Instance);
    }

    [Theory]
    [InlineData(ReportAiDecision.AiDisabled, ReportFallbackReasons.AiDisabled)]
    [InlineData(ReportAiDecision.SandboxOnly, ReportFallbackReasons.SandboxOnly)]
    [InlineData(ReportAiDecision.MonthlyLimit, ReportFallbackReasons.MonthlyLimit)]
    [InlineData(ReportAiDecision.DeploymentCap, ReportFallbackReasons.DeploymentCap)]
    public async Task A_company_not_admitted_gets_the_template_and_the_model_is_never_called(ReportAiDecision decision, string reason)
    {
        var client = new ScriptedClient(Says(Answer()));

        var result = await Composer(client, new FakeAdmission(decision)).ComposeAsync(Tenant, false, Pack, []);

        Assert.Equal((ReportGenerator.Template, reason), (result.Generator, result.FallbackReason));
        Assert.Empty(client.Requests);
    }

    [Theory]
    [InlineData(LlmFailureKind.Unavailable)]
    [InlineData(LlmFailureKind.Timeout)]
    [InlineData(LlmFailureKind.RateLimited)]
    [InlineData(LlmFailureKind.Misconfigured)]
    public async Task A_provider_that_is_down_gives_the_template_at_once(LlmFailureKind kind)
    {
        var client = new ScriptedClient(Throws(kind));

        var result = await Composer(client, new FakeAdmission(ReportAiDecision.Allowed)).ComposeAsync(Tenant, false, Pack, []);

        Assert.Equal((ReportGenerator.Template, ReportFallbackReasons.ProviderUnavailable, 1), (result.Generator, result.FallbackReason, result.LlmAttempts));
        Assert.Single(client.Requests);
    }

    [Fact]
    public async Task Two_invalid_answers_give_the_template_and_both_calls_are_paid()
    {
        var client = new ScriptedClient(Says(Answer("La conversión fue de 37 %.")), Says("esto no es JSON"));

        var result = await Composer(client, new FakeAdmission(ReportAiDecision.Allowed)).ComposeAsync(Tenant, false, Pack, []);

        Assert.Equal((ReportGenerator.Template, ReportFallbackReasons.InvalidOutput, 2), (result.Generator, result.FallbackReason, result.LlmAttempts));
        Assert.Equal(new LlmUsage(2000, 1000, 4000, 0), result.Usage);
        Assert.Equal(LlmPricing.Default.Estimate(new LlmUsage(2000, 1000, 4000, 0)), result.EstimatedUsd);
    }

    [Theory]
    [InlineData(LlmFailureKind.Refused)]
    [InlineData(LlmFailureKind.Truncated)]
    [InlineData(LlmFailureKind.InvalidOutput)]
    public async Task An_unusable_answer_is_regenerated_once_and_a_good_second_answer_is_used(LlmFailureKind kind)
    {
        var client = new ScriptedClient(Throws(kind), Says(Answer()));

        var result = await Composer(client, new FakeAdmission(ReportAiDecision.Allowed)).ComposeAsync(Tenant, false, Pack, []);

        Assert.Equal((ReportGenerator.Llm, 2), (result.Generator, result.LlmAttempts));
    }

    [Fact]
    public async Task A_truncated_answer_or_a_name_outside_the_pack_is_rejected()
    {
        var truncated = new ScriptedClient(() => new LlmCompletion(LlmStopReason.MaxTokens, Answer(), Usage, "m"));
        Assert.Equal(ReportGenerator.Template, (await Composer(truncated, new FakeAdmission(ReportAiDecision.Allowed)).ComposeAsync(Tenant, false, Pack, [])).Generator);

        var outside = new ScriptedClient(Says(Answer(names: ["Norte", "Kennedy"])));
        Assert.Equal(ReportGenerator.Template, (await Composer(outside, new FakeAdmission(ReportAiDecision.Allowed)).ComposeAsync(Tenant, false, Pack, [])).Generator);

        var otherBranch = new ScriptedClient(Says(Answer("Sur no tuvo conversaciones. La conversión fue de 23,5 %.")));
        Assert.Equal(ReportGenerator.Template,
            (await Composer(otherBranch, new FakeAdmission(ReportAiDecision.Allowed)).ComposeAsync(Tenant, false, Pack, ["Sur"])).Generator);
    }

    [Fact]
    public void The_request_carries_the_schema_and_a_stable_hash_of_the_pack()
    {
        var first = LlmReportWriter.BuildRequest(Pack);
        var second = LlmReportWriter.BuildRequest(Pack);

        Assert.Equal(first.SnapshotHash, second.SnapshotHash);
        Assert.NotEqual(first.SnapshotHash, LlmReportWriter.BuildRequest(Pack with { Company = "Otra" }).SnapshotHash);
        Assert.Equal(6, first.Schema["required"]!.AsArray().Count);
        Assert.Null(LlmReportWriter.Parse("{\"title\": 3}"));
        Assert.Null(LlmReportWriter.Parse(null));
        Assert.NotNull(LlmReportWriter.Parse(Answer()));
    }

    private sealed class ContentComparer : IEqualityComparer<ReportContent>
    {
        public static ContentComparer Instance { get; } = new();

        public bool Equals(ReportContent? x, ReportContent? y) => x?.ToJson() == y?.ToJson();

        public int GetHashCode(ReportContent obj) => obj.ToJson().GetHashCode();
    }
}
