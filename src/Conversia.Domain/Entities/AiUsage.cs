namespace Conversia.Domain.Entities;

/// <summary>
/// What the LLM cost one company in one month (America/Bogota), kept apart for its sandbox conversations (design D13; docs/CLASSIFICATION.md,
/// "Usage and quota"). Written only by the usage meter with atomic upserts; never text, only counts, tokens and money. Kept for good (the
/// billing evidence); the distinct conversation set (<see cref="AiUsageConversation"/>) is not.
/// </summary>
public class AiUsageMonth
{
    public Guid TenantId { get; private set; }

    /// <summary>The first day of the month, in America/Bogota.</summary>
    public DateOnly Month { get; private set; }

    /// <summary>Sandbox usage is recorded but never counts against the company's billable quota or ceiling.</summary>
    public bool IsSandbox { get; private set; }

    /// <summary>Distinct conversations the LLM read successfully this month: the unit of the quota.</summary>
    public int ConversationsAnalyzed { get; private set; }

    public int Calls { get; private set; }

    public int FailedCalls { get; private set; }

    public long InputTokens { get; private set; }

    public long OutputTokens { get; private set; }

    public long CacheReadTokens { get; private set; }

    public long CacheWriteTokens { get; private set; }

    /// <summary>Estimated from the configured prices (<c>Classification:Llm:Pricing:*</c>): an estimate for the operator, not a bill.</summary>
    public decimal EstimatedUsd { get; private set; }

    /// <summary>Calls whose reading disagreed with the rules about a sale (Won guard).</summary>
    public int WonDisagreements { get; private set; }

    /// <summary>When the quota was reached this month (set once).</summary>
    public DateTime? ExhaustedAt { get; private set; }

    /// <summary>When the company's USD ceiling was reached this month (set once).</summary>
    public DateTime? UsdCeilingReachedAt { get; private set; }

    public DateTime UpdatedAt { get; private set; }

    // Required by EF Core for materialization; rows are written by SQL upserts.
    private AiUsageMonth()
    {
    }
}

/// <summary>One conversation the LLM read successfully in a month: makes the count distinct. Kept for the current and the previous month only.</summary>
public class AiUsageConversation
{
    public Guid TenantId { get; private set; }

    public DateOnly Month { get; private set; }

    public Guid ConversationId { get; private set; }

    private AiUsageConversation()
    {
    }
}

/// <summary>
/// How many manual sends of a company ended each way on one day (America/Bogota): <c>sent</c> or the stable problem code of the refusal
/// (<c>provider_limit_reached</c>, ...). Counts only, for the platform's monitoring; written by an atomic upsert.
/// </summary>
public class MessageSendOutcome
{
    public const int OutcomeMaxLength = 64;

    public const string Sent = "sent";

    public const string Unexpected = "unexpected_error";

    public Guid TenantId { get; private set; }

    public DateOnly Day { get; private set; }

    public bool IsSandbox { get; private set; }

    public string Outcome { get; private set; } = string.Empty;

    public int Count { get; private set; }

    private MessageSendOutcome()
    {
    }
}

/// <summary>
/// What the AI sales assistant's model cost one company in one month (America/Bogota), apart from the conversation analysis (docs/ASSISTANT.md,
/// "Costs"): its own category, with its own per-company monthly cap; it counts against the deployment's cap with the analysis. Sandbox usage
/// is kept in its own row. Written only by the assistant's usage meter with atomic upserts; never text.
/// </summary>
public class AssistantUsageMonth
{
    public Guid TenantId { get; private set; }

    /// <summary>The first day of the month, in America/Bogota.</summary>
    public DateOnly Month { get; private set; }

    public bool IsSandbox { get; private set; }

    /// <summary>Answers prepared with the model (a guard call, a reply call and a regeneration count as one).</summary>
    public int Generations { get; private set; }

    /// <summary>Calls to the model (guard and reply).</summary>
    public int Calls { get; private set; }

    public int GuardCalls { get; private set; }

    public int FailedCalls { get; private set; }

    public long InputTokens { get; private set; }

    public long OutputTokens { get; private set; }

    public long CacheReadTokens { get; private set; }

    public long CacheWriteTokens { get; private set; }

    /// <summary>Estimated from the configured prices (<c>Assistant:Llm:Pricing:*</c>): an estimate for the operator, not a bill.</summary>
    public decimal EstimatedUsd { get; private set; }

    /// <summary>When the company's monthly cap of the assistant was reached (set once).</summary>
    public DateTime? CapReachedAt { get; private set; }

    public DateTime UpdatedAt { get; private set; }

    private AssistantUsageMonth()
    {
    }
}
