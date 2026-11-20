using Conversia.Domain.Enums;

namespace Conversia.Application.Classification.Llm;

/// <summary>The wire codes of the extraction answer and the domain values they stand for. One table, read by the schema and the mapper.</summary>
internal static class ExtractionVocabulary
{
    public static readonly IReadOnlyList<(string Code, ClassificationOutcome Value)> Stages =
    [
        ("undetermined", ClassificationOutcome.Undetermined),
        ("in_follow_up", ClassificationOutcome.InFollowUp),
        ("won", ClassificationOutcome.Won),
        ("lost", ClassificationOutcome.Lost),
    ];

    /// <summary>Every loss reason but <see cref="LossReason.NoResponse"/>, which only the close-by-inactivity job sets.</summary>
    public static readonly IReadOnlyList<(string Code, LossReason Value)> LossReasons =
    [
        ("price", LossReason.Price),
        ("availability", LossReason.Availability),
        ("location", LossReason.Location),
        ("timing", LossReason.Timing),
        ("competitor", LossReason.Competitor),
        ("not_interested", LossReason.NotInterested),
        ("other", LossReason.Other),
        ("unknown", LossReason.Unknown),
    ];

    public static readonly IReadOnlyList<(string Code, ObjectionType Value)> Objections =
    [
        ("none", ObjectionType.None),
        ("price", ObjectionType.Price),
        ("timing", ObjectionType.Timing),
        ("location", ObjectionType.Location),
        ("trust", ObjectionType.Trust),
        ("pain", ObjectionType.Pain),
        ("other", ObjectionType.Other),
    ];

    public static readonly IReadOnlyList<(string Code, Gender Value)> Genders =
    [
        ("undetermined", Gender.Undetermined),
        ("female", Gender.Female),
        ("male", Gender.Male),
        ("other", Gender.Other),
    ];

    public static readonly IReadOnlyList<(string Code, AgeRange Value)> AgeRanges =
    [
        ("under_18", AgeRange.Under18),
        ("18_24", AgeRange.From18To24),
        ("25_34", AgeRange.From25To34),
        ("35_44", AgeRange.From35To44),
        ("45_54", AgeRange.From45To54),
        ("55_plus", AgeRange.From55),
    ];

    /// <summary>The closing point's steps, in their order (docs/CLASSIFICATION.md, "Closing point").</summary>
    public static readonly IReadOnlyList<(string Code, ReachedStage Value)> ReachedStages =
    [
        ("first_contact", ReachedStage.FirstContact),
        ("needs_identified", ReachedStage.NeedsIdentified),
        ("price_shared", ReachedStage.PriceShared),
        ("objection_raised", ReachedStage.ObjectionRaised),
        ("appointment_proposed", ReachedStage.AppointmentProposed),
        ("appointment_booked", ReachedStage.AppointmentBooked),
        ("payment_or_deposit", ReachedStage.PaymentOrDeposit),
    ];

    public static readonly IReadOnlyList<(string Code, OffChannelMove Value)> OffChannelMoves =
    [
        ("none", OffChannelMove.None),
        ("phone_call", OffChannelMove.PhoneCall),
        ("in_person", OffChannelMove.InPerson),
        ("other_channel", OffChannelMove.OtherChannel),
    ];

    /// <summary>Every closer but <see cref="ClosedBy.AiAssistant"/>: reserved for the AI sales assistant (roadmap stage 5), never the model's answer.</summary>
    public static readonly IReadOnlyList<(string Code, ClosedBy Value)> Closers =
    [
        ("unknown", ClosedBy.Unknown),
        ("advisor", ClosedBy.Advisor),
        ("customer_self_service", ClosedBy.CustomerSelfService),
    ];

    /// <summary>The answer's properties of the closing point; an answer with none of them leaves the closing point unread.</summary>
    public static readonly IReadOnlyList<string> ClosingPointProperties =
        ["reached_stage", "moved_off_channel", "off_channel_evidence_line", "closed_by", "closed_outside_system"];

    /// <summary>The most service codes one answer may hold (as many as the rules classifier keeps).</summary>
    public const int MaxServices = 5;
}
