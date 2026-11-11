namespace Conversia.Application.Classification.Llm;

/// <summary>
/// The closed list of service and interest-reason codes the model may answer (design D7): the codes of the deployment's rules file (services,
/// general services, interest reasons), in the file's order, plus <see cref="Other"/>. Built once per deployment, so it is static per
/// deployment and part of the cacheable prompt prefix.
/// </summary>
public sealed record LlmCatalog(IReadOnlyList<string> Services, IReadOnlyList<string> InterestReasons)
{
    /// <summary>The code of anything outside the list. A service outside the list is coerced to it; an interest reason must be in the list.</summary>
    public const string Other = "other";

    public static LlmCatalog FromRules(ClassificationRuleSet rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        static IReadOnlyList<string> WithOther(IEnumerable<string?> codes)
            => [.. codes.OfType<string>().Where(c => c != Other).Distinct(StringComparer.Ordinal), Other];

        return new LlmCatalog(
            WithOther(rules.Services.Concat(rules.GeneralServices).Select(s => s.Code)),
            WithOther(rules.InterestReasons.Select(s => s.Code)));
    }
}
