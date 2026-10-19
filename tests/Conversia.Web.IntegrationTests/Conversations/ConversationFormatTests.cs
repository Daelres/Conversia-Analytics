using Conversia.Application.DTOs.Conversations;
using Conversia.Web.Services;

namespace Conversia.Web.IntegrationTests.Conversations;

/// <summary>How the conversation pages write customers, ages and the filters of the address. Pure functions: no host.</summary>
public class ConversationFormatTests
{
    [Theory]
    [InlineData("573001234567", "…4567")]
    [InlineData("+573001234567", "…4567")]
    [InlineData("1234", "…1234")]
    [InlineData("12", "…12")]
    [InlineData("", "…")]
    [InlineData(null, "…")]
    public void A_phone_is_masked_down_to_its_last_four_digits(string? waId, string expected)
        => Assert.Equal(expected, ConversationFormat.MaskedPhone(waId));

    [Fact]
    public void The_masked_phone_never_contains_the_rest_of_the_number()
        => Assert.DoesNotContain("30012", ConversationFormat.MaskedPhone("573001234567"));

    [Fact]
    public void A_customer_is_listed_by_the_name_whatsapp_gave_or_by_the_masked_number()
    {
        Assert.Equal("Camila R.", ConversationFormat.ListTitle(new ContactSummary { WaId = "573001234567", DisplayName = "  Camila R. " }));
        Assert.Equal("Cliente …4567", ConversationFormat.ListTitle(new ContactSummary { WaId = "573001234567", DisplayName = null }));
        Assert.Equal("Cliente …4567", ConversationFormat.ListTitle(new ContactSummary { WaId = "573001234567", DisplayName = "   " }));
    }

    [Theory]
    [InlineData(0, "ahora")]
    [InlineData(-30, "ahora")]
    [InlineData(59, "ahora")]
    [InlineData(300, "hace 5\u00A0min")]
    [InlineData(3 * 3600, "hace 3\u00A0h")]
    [InlineData(3 * 3600 + 3000, "hace 3\u00A0h")]
    [InlineData(2 * 86400 + 7200, "hace 2\u00A0d")]
    public void An_age_is_one_unit_the_biggest_that_fits(int seconds, string expected)
    {
        var now = new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal(expected, ConversationFormat.Age(now.AddSeconds(-seconds), now));
    }

    [Fact]
    public void The_first_response_time_needs_both_ends()
    {
        var start = new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal(15, ConversationFormat.FirstResponseMinutes(new ConversationListItem { FirstInboundAt = start, FirstResponseAt = start.AddMinutes(15) }));
        Assert.Null(ConversationFormat.FirstResponseMinutes(new ConversationListItem { FirstInboundAt = start }));
        Assert.Null(ConversationFormat.FirstResponseMinutes(new ConversationListItem { FirstResponseAt = start }));
        Assert.Equal(0, ConversationFormat.FirstResponseMinutes(new ConversationListItem { FirstInboundAt = start, FirstResponseAt = start.AddSeconds(-5) }));
    }

    [Theory]
    [InlineData(0.85, "85 %")]
    [InlineData(1.0, "100 %")]
    [InlineData(0.005, "1 %")]
    [InlineData(null, "—")]
    public void Classifier_confidence_is_a_percentage_of_the_zero_to_one_scale(double? confidence, string expected)
        => Assert.Equal(expected, ConversationFormat.Confidence((decimal?)confidence));

    [Theory]
    [InlineData("text", "Texto")]
    [InlineData("Image", "Imagen")]
    [InlineData("audio", "Audio")]
    [InlineData(null, "Mensaje")]
    [InlineData("flying_saucer", "Mensaje (flying_saucer)")]
    public void Message_types_are_named_in_spanish(string? type, string expected)
        => Assert.Equal(expected, ConversationFormat.MessageType(type));

    [Fact]
    public void A_user_id_is_shortened_to_eight_characters()
        => Assert.Equal("a0b1c2d3", ConversationFormat.ShortId(Guid.Parse("a0b1c2d3-1111-2222-3333-444455556666")));

    // ---- filters of the address ----

    [Fact]
    public void An_empty_address_is_unfiltered_on_the_first_page()
    {
        var filters = ConversationFilters.Parse(null, null, null, null, null, null, null, null, null, null);

        Assert.False(filters.IsFiltered);
        Assert.Equal(1, filters.Page);
        Assert.Equal("/conversaciones", filters.ToUrl());
    }

    [Fact]
    public void Every_filter_survives_a_round_trip_through_the_address()
    {
        var branch = Guid.NewGuid();
        var advisor = Guid.NewGuid();
        var filters = ConversationFilters.Parse(branch.ToString(), advisor.ToString(), "Lost", "Ads", "Closed", "2026-09-01", "2026-10-01", "closed-unanswered", null, "3");

        var url = filters.ToUrl(filters.Page);
        var query = System.Web.HttpUtility.ParseQueryString(new Uri("https://x" + url).Query);
        var again = ConversationFilters.Parse(query["branch"], query["advisor"], query["stage"], query["source"], query["status"], query["from"], query["to"], query["reply"], query["unanswered"], query["page"]);

        Assert.Equal(filters, again);
        Assert.True(again.IsFiltered);
        Assert.Equal(3, again.Page);
        Assert.True(again.ClosedUnanswered);
        Assert.False(again.UnansweredOpen);
    }

    [Theory]
    [InlineData("not-a-guid", "none-of-these", "Archived", "Smoke", "Gone", "yesterday", "2026-13-45")]
    [InlineData("", "", "", "", "", "", "")]
    [InlineData("00000000-0000-0000-0000-000000000000", null, "won", "ads", "open", "18/10/2026", "2026-1-5")]
    public void Values_the_api_does_not_know_are_dropped_instead_of_sent(string? branch, string? advisor, string? stage, string? source, string? status, string? from, string? to)
    {
        var filters = ConversationFilters.Parse(branch, advisor, stage, source, status, from, to, null, null, "x");

        Assert.False(filters.IsFiltered);
        Assert.Equal(1, filters.Page);
    }

    [Fact]
    public void The_none_advisor_asks_for_the_unassigned_and_the_old_unanswered_flag_still_works()
    {
        var unassigned = ConversationFilters.Parse(null, ConversationFilters.NoAdvisor, null, null, null, null, null, null, null, null);
        var legacy = ConversationFilters.Parse(null, null, null, null, null, null, null, null, "true", null);

        Assert.True(unassigned.Unassigned);
        Assert.Null(unassigned.AdvisorId);
        Assert.True(unassigned.ToQuery().Unassigned);
        Assert.True(legacy.UnansweredOpen);
        Assert.Equal("/conversaciones?reply=unanswered", legacy.ToUrl());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("abc")]
    [InlineData("99999999999999999999")]
    public void A_bad_page_number_is_the_first_page(string page)
        => Assert.Equal(1, ConversationFilters.Parse(null, null, null, null, null, null, null, null, null, page).Page);
}
