namespace Conversia.Domain.Enums;

/// <summary>How often a manager report is generated (docs/REPORTS.md, "Schedule"). Persisted by name.</summary>
public enum ReportFrequency
{
    /// <summary>Covers the previous day; sent every day at the chosen hour.</summary>
    Daily = 1,

    /// <summary>Covers the previous Monday-to-Sunday week; sent on the chosen day of the following week (Monday by default).</summary>
    Weekly = 2,

    /// <summary>Covers the previous calendar month; sent on the first day of the month.</summary>
    Monthly = 3,
}

/// <summary>
/// The parts of the analysis a report may contain: a closed list (docs/REPORTS.md, "Sections"). Persisted as the integer of the flags, so the
/// numbers are part of the storage contract: never renumber them.
/// </summary>
[Flags]
public enum ReportSections
{
    None = 0,

    /// <summary>The headline indicators: conversations, sales and conversion against the previous period.</summary>
    Headline = 1,

    /// <summary>Conversion by branch, advisor and source.</summary>
    Conversion = 2,

    /// <summary>The step where most conversations without a sale stopped.</summary>
    DropOff = 4,

    /// <summary>The main reasons for losing a sale and the objections raised.</summary>
    LossReasons = 8,

    /// <summary>First response time and the conversations nobody has answered.</summary>
    Response = 16,

    /// <summary>A note on how much of the period the AI has read.</summary>
    AiCoverage = 32,

    All = Headline | Conversion | DropOff | LossReasons | Response | AiCoverage,
}

/// <summary>How a recipient receives a report. Persisted as the integer of the flags.</summary>
[Flags]
public enum ReportChannels
{
    None = 0,
    Email = 1,
    WhatsApp = 2,
    Both = Email | WhatsApp,
}

/// <summary>One way a report reaches one recipient: a delivery row per recipient and channel. Persisted by name.</summary>
public enum ReportDeliveryChannel
{
    Email = 1,
    WhatsApp = 2,
}

/// <summary>Who wrote a stored report. Persisted by name.</summary>
public enum ReportGenerator
{
    /// <summary>The deterministic Spanish template built from the data pack (always available).</summary>
    Template = 1,

    /// <summary>The language model, validated against the data pack.</summary>
    Llm = 2,
}

/// <summary>Why a report was written: for a scheduled period, or as a preview. Persisted by name.</summary>
public enum ReportCompositionKind
{
    Scheduled = 1,
    Preview = 2,
}

/// <summary>Where one delivery stands. Persisted by name.</summary>
public enum ReportDeliveryStatus
{
    /// <summary>Waiting for its (next) attempt.</summary>
    Pending = 1,

    Sent = 2,

    /// <summary>Given up: a permanent refusal, or every attempt failed.</summary>
    Failed = 3,

    /// <summary>Never attempted: the recipient cannot receive it on this channel (no opt-in, unsubscribed, no template).</summary>
    Skipped = 4,
}
