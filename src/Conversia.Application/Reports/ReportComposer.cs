using Conversia.Application.Classification.Llm;
using Conversia.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Conversia.Application.Reports;

/// <summary>Whether a company's next report may be written by the model (docs/REPORTS.md, "Cost and quota").</summary>
public enum ReportAiDecision
{
    Allowed,

    /// <summary>The AI is switched off for the company (the administrator's switch, the same as the conversation analysis).</summary>
    AiDisabled,

    /// <summary>The real provider reaches only companies in sandbox mode until the privacy gate is lifted (docs/CLASSIFICATION.md).</summary>
    SandboxOnly,

    /// <summary>The company used this month's model-written reports (<c>Reports:Ai:MonthlyReportsPerCompany</c>).</summary>
    MonthlyLimit,

    /// <summary>The deployment's monthly spend on reports is used up (<c>Reports:Ai:DeploymentMonthlyUsdCap</c>).</summary>
    DeploymentCap,
}

/// <summary>The admission of the report writer: a cheap read, apart from the conversation quota (reports never consume it).</summary>
public interface IReportAiAdmission
{
    Task<ReportAiDecision> DecideAsync(Guid tenantId, bool sandboxMode, CancellationToken cancellationToken = default);
}

/// <summary>Who wrote a report and what it cost.</summary>
/// <param name="FallbackReason">Why the template wrote it; null when the model did.</param>
public sealed record ReportComposition(
    ReportContent Content, ReportGenerator Generator, string? FallbackReason, int LlmAttempts, string? Model, LlmUsage Usage, decimal EstimatedUsd);

/// <summary>The stable codes of <see cref="ReportComposition.FallbackReason"/>.</summary>
public static class ReportFallbackReasons
{
    public const string NotConfigured = "ai_not_configured";
    public const string AiDisabled = "ai_disabled";
    public const string SandboxOnly = "sandbox_only";
    public const string MonthlyLimit = "monthly_limit";
    public const string DeploymentCap = "deployment_cap";
    public const string ProviderUnavailable = "provider_unavailable";
    public const string InvalidOutput = "invalid_output";

    /// <summary>The company used today's previews written by the model (<c>Reports:PreviewsPerCompanyPerDay</c>).</summary>
    public const string PreviewBudget = "preview_budget";

    /// <summary>The model was already called for this period and the report was not stored (a failure after the call): not called again.</summary>
    public const string ModelAlreadyCalled = "model_already_called";

    public static string Of(ReportAiDecision decision) => decision switch
    {
        ReportAiDecision.AiDisabled => AiDisabled,
        ReportAiDecision.SandboxOnly => SandboxOnly,
        ReportAiDecision.MonthlyLimit => MonthlyLimit,
        ReportAiDecision.DeploymentCap => DeploymentCap,
        _ => NotConfigured,
    };
}

/// <summary>
/// Writes a report (docs/REPORTS.md, "Generation"): the model when it is configured and the company is admitted, validated against the data
/// pack, with one more attempt when its answer is unusable; the deterministic template otherwise. Never throws for the model: a provider that
/// is down, a refusal or two invalid answers end in the template, which is always available.
/// </summary>
public sealed class ReportComposer
{
    /// <summary>One attempt and one regeneration.</summary>
    public const int MaxAttempts = 2;

    public static readonly EventId ReportAnswerRejected = new(4302, "ReportAnswerRejected");

    private readonly ILlmClient? _client;
    private readonly IReportAiAdmission _admission;
    private readonly LlmPricing _pricing;
    private readonly ILogger<ReportComposer> _logger;

    public ReportComposer(ILlmClient? client, IReportAiAdmission admission, LlmPricing pricing, ILogger<ReportComposer> logger)
    {
        _client = client;
        _admission = admission;
        _pricing = pricing;
        _logger = logger;
    }

    public async Task<ReportComposition> ComposeAsync(
        Guid tenantId, bool sandboxMode, ReportDataPack pack, IReadOnlyCollection<string> namesOutsidePack, CancellationToken cancellationToken = default,
        string? modelBlockedReason = null)
    {
        ArgumentNullException.ThrowIfNull(pack);
        if (modelBlockedReason is not null)
        {
            return Template(pack, modelBlockedReason, 0, null, LlmUsage.None);
        }

        if (_client is null)
        {
            return Template(pack, ReportFallbackReasons.NotConfigured, 0, null, LlmUsage.None);
        }

        var decision = await _admission.DecideAsync(tenantId, sandboxMode, cancellationToken);
        if (decision != ReportAiDecision.Allowed)
        {
            return Template(pack, ReportFallbackReasons.Of(decision), 0, null, LlmUsage.None);
        }

        var request = LlmReportWriter.BuildRequest(pack);
        var usage = LlmUsage.None;
        string? model = null;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            LlmCompletion completion;
            try
            {
                completion = await _client.CompleteAsync(request, cancellationToken);
            }
            catch (LlmCallException ex) when (ex.Kind.IsTransport() || ex.Kind == LlmFailureKind.Misconfigured)
            {
                _logger.LogWarning(ReportAnswerRejected, "The report model of tenant {TenantId} could not be reached ({Kind}): the template writes the report.", tenantId, ex.Kind);
                return Template(pack, ReportFallbackReasons.ProviderUnavailable, attempt, model, usage);
            }
            catch (LlmCallException ex)
            {
                _logger.LogWarning(ReportAnswerRejected, "Attempt {Attempt} of the report of tenant {TenantId} gave no usable answer ({Kind}).", attempt, tenantId, ex.Kind);
                continue;
            }

            usage = Add(usage, completion.Usage);
            model = completion.ModelId;
            var answer = completion.StopReason == LlmStopReason.EndTurn ? LlmReportWriter.Parse(completion.JsonText) : null;
            if (answer is null)
            {
                _logger.LogWarning(ReportAnswerRejected, "Attempt {Attempt} of the report of tenant {TenantId} gave no usable answer ({StopReason}).", attempt, tenantId, completion.StopReason);
                continue;
            }

            var grounding = ReportGroundingValidator.Validate(answer.Content, pack, answer.MentionedNames, namesOutsidePack);
            if (grounding.IsValid)
            {
                return new ReportComposition(answer.Content, ReportGenerator.Llm, null, attempt, model, usage, _pricing.Estimate(usage));
            }

            // Codes only (number:37, name:X): the names are the company's own, never a customer's, and no sentence is logged.
            _logger.LogWarning(ReportAnswerRejected, "Attempt {Attempt} of the report of tenant {TenantId} was not grounded in its data: {Problems}.",
                attempt, tenantId, string.Join(", ", grounding.Problems.Take(10)));
        }

        return Template(pack, ReportFallbackReasons.InvalidOutput, MaxAttempts, model, usage);
    }

    private ReportComposition Template(ReportDataPack pack, string reason, int attempts, string? model, LlmUsage usage)
        => new(TemplateReportWriter.Write(pack), ReportGenerator.Template, reason, attempts, model, usage, _pricing.Estimate(usage));

    private static LlmUsage Add(LlmUsage a, LlmUsage b)
        => new(a.InputTokens + b.InputTokens, a.OutputTokens + b.OutputTokens, a.CacheReadTokens + b.CacheReadTokens, a.CacheWriteTokens + b.CacheWriteTokens);
}
