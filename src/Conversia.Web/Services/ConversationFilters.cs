using System.Globalization;
using Conversia.Application.DTOs.Conversations;

namespace Conversia.Web.Services;

/// <summary>
/// What the conversation list is filtered by, read from the page's address. Anything that is not a value the API knows (a malformed id,
/// a stage that does not exist, a date that is not <c>yyyy-MM-dd</c>) is dropped instead of reaching the API, so a hand-edited address
/// shows the list unfiltered rather than an error. Dates are local dates of the tenant's zone, <c>To</c> exclusive.
/// </summary>
public sealed record ConversationFilters
{
    public const int PageSize = 20;

    /// <summary>The <c>advisor</c> value that asks for the conversations nobody has taken.</summary>
    public const string NoAdvisor = "none";

    /// <summary>The <c>reply</c> values: still open and waiting for a first answer, or closed without ever getting one.</summary>
    public const string ReplyOpen = "unanswered";

    public const string ReplyClosed = "closed-unanswered";

    public Guid? BranchId { get; init; }

    public Guid? AdvisorId { get; init; }

    public bool Unassigned { get; init; }

    public string? Stage { get; init; }

    public string? Source { get; init; }

    public string? Status { get; init; }

    public DateOnly? From { get; init; }

    public DateOnly? To { get; init; }

    /// <summary>The customer wrote, nobody answered and the conversation is still open: somebody can still reply.</summary>
    public bool UnansweredOpen { get; init; }

    /// <summary>The customer wrote and the conversation closed without a reply.</summary>
    public bool ClosedUnanswered { get; init; }

    public int Page { get; init; } = 1;

    /// <summary>The address' values as text, the way the query string carries them.</summary>
    public static ConversationFilters Parse(
        string? branch, string? advisor, string? stage, string? source, string? status, string? from, string? to, string? reply, string? legacyUnanswered, string? page)
    {
        var unassigned = string.Equals(advisor, NoAdvisor, StringComparison.Ordinal);
        var openUnanswered = reply == ReplyOpen || IsTrue(legacyUnanswered);
        return new ConversationFilters
        {
            BranchId = ParseGuid(branch),
            AdvisorId = unassigned ? null : ParseGuid(advisor),
            Unassigned = unassigned,
            Stage = OneOf(stage, ConversationLabels.Stages),
            Source = OneOf(source, ConversationLabels.Sources),
            Status = OneOf(status, ConversationLabels.Statuses),
            From = ParseDate(from),
            To = ParseDate(to),
            UnansweredOpen = openUnanswered,
            ClosedUnanswered = !openUnanswered && reply == ReplyClosed,
            Page = int.TryParse(page, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= 1 ? number : 1,
        };
    }

    public bool IsFiltered
        => BranchId is not null || AdvisorId is not null || Unassigned || Stage is not null || Source is not null || Status is not null
           || From is not null || To is not null || UnansweredOpen || ClosedUnanswered;

    public ConversationQuery ToQuery() => new()
    {
        Page = Page,
        PageSize = PageSize,
        BranchId = BranchId,
        AdvisorId = AdvisorId,
        Unassigned = Unassigned ? true : null,
        Stage = Stage,
        Source = Source,
        Status = Status,
        From = From,
        To = To,
        Unanswered = UnansweredOpen ? true : null,
        ClosedUnanswered = ClosedUnanswered ? true : null,
    };

    /// <summary>The address of the list with these filters on <paramref name="page"/> (the first page carries no <c>page</c>).</summary>
    public string ToUrl(int page = 1) => ConversationLinks.List(this with { Page = page });

    private static bool IsTrue(string? value) => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    private static Guid? ParseGuid(string? value) => Guid.TryParse(value, out var id) && id != Guid.Empty ? id : null;

    private static string? OneOf(string? value, IReadOnlyList<string> allowed) => allowed.FirstOrDefault(name => string.Equals(name, value, StringComparison.Ordinal));

    private static DateOnly? ParseDate(string? value)
        => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
}
