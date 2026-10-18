namespace Conversia.Infrastructure.Configuration;

/// <summary>
/// The validated <c>Conversations</c> section: the job that closes conversations nobody has written in for a while.
/// A closed conversation takes no more messages; the customer's next message opens a new one.
/// </summary>
public sealed record ConversationLifecycleOptions(bool Enabled, int AfterDays, int IntervalMinutes, int BatchSize)
{
    public const string SectionName = "Conversations";
    public const string EnabledKey = SectionName + ":AutoCloseEnabled";
    public const string AfterDaysKey = SectionName + ":AutoCloseAfterDays";
    public const string IntervalMinutesKey = SectionName + ":AutoCloseIntervalMinutes";
    public const string BatchSizeKey = SectionName + ":AutoCloseBatchSize";

    public static ConversationLifecycleOptions Default { get; } = new(Enabled: true, AfterDays: 7, IntervalMinutes: 15, BatchSize: 200);

    public TimeSpan Interval => TimeSpan.FromMinutes(IntervalMinutes);

    public TimeSpan CloseAfter => TimeSpan.FromDays(AfterDays);
}
