namespace Conversia.Application.Classification.Llm;

/// <summary>
/// USD per million tokens of each kind (<c>Classification:Llm:Pricing:*</c>), for the estimated cost in the log line of each call. Defaults:
/// Claude Haiku 5.5 at prompts up to 100K tokens (input 0.10, output 0.50, cache read 0.01 = 0.1x input, 5-minute cache write 0.125 =
/// 1.25x input). An estimate for the operator, not a bill: check the provider's price page before budgeting on it.
/// </summary>
public sealed record LlmPricing(decimal InputPerMTok, decimal OutputPerMTok, decimal CacheReadPerMTok, decimal CacheWritePerMTok)
{
    public static LlmPricing Default { get; } = new(InputPerMTok: 0.10m, OutputPerMTok: 0.50m, CacheReadPerMTok: 0.01m, CacheWritePerMTok: 0.125m);

    public decimal Estimate(LlmUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        return ((usage.InputTokens * InputPerMTok) + (usage.OutputTokens * OutputPerMTok) + (usage.CacheReadTokens * CacheReadPerMTok)
            + (usage.CacheWriteTokens * CacheWritePerMTok)) / 1_000_000m;
    }
}
