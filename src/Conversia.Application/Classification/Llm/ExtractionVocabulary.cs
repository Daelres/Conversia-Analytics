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

    /// <summary>The most service codes one answer may hold (as many as the rules classifier keeps).</summary>
    public const int MaxServices = 5;
}
