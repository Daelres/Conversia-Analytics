using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Domain.Exceptions;

namespace Conversia.Domain.UnitTests.Entities;

public class AssistantSettingsTests
{
    private static readonly DateTime T0 = new(2026, 11, 19, 15, 0, 0, DateTimeKind.Utc);

    private static AssistantSettingsValues Values(
        int delay = 60, AssistantActiveHours hours = AssistantActiveHours.Always, int? start = null, int? end = null, int turns = 8, int cooldown = 60,
        AssistantDisclosureMode disclosure = AssistantDisclosureMode.OnRequest, string? disclosureText = "Soy el asistente virtual.",
        string? handoffLine = "Ya te comunico con un asesor.", IReadOnlyList<string>? keywords = null, string? tone = null, string? language = "es-CO")
        => new(delay, hours, start, end, turns, cooldown, disclosure, disclosureText, handoffLine, keywords ?? ["queja"], tone, language);

    [Fact]
    public void Defaults_are_off_one_minute_disclose_on_request_and_the_default_line()
    {
        var s = AssistantSettings.Create(Guid.NewGuid());
        Assert.False(s.Enabled);
        Assert.Equal(60, s.ReplyDelaySeconds);
        Assert.Equal(AssistantDisclosureMode.OnRequest, s.DisclosureMode);
        Assert.Equal(AssistantSettings.DefaultHandoffLine, s.HandoffLine);
        Assert.Equal(AssistantActiveHours.Always, s.ActiveHours);
        Assert.Equal("es-CO", s.Language);
        Assert.Contains("queja", s.HandoffKeywords);
    }

    [Theory]
    [InlineData(29)]
    [InlineData(901)]
    public void The_delay_is_between_30_and_900_seconds(int delay)
    {
        var s = AssistantSettings.Create(Guid.NewGuid());
        var ex = Assert.Throws<DomainException>(() => s.Update(Values(delay: delay)));
        Assert.Equal(AssistantSettings.ReplyDelayRuleMessage, ex.Message);
        Assert.Equal(60, s.ReplyDelaySeconds);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(900)]
    public void The_delay_bounds_are_allowed(int delay)
    {
        var s = AssistantSettings.Create(Guid.NewGuid());
        s.Update(Values(delay: delay));
        Assert.Equal(delay, s.ReplyDelaySeconds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public void The_turns_are_bounded(int turns)
        => Assert.Throws<DomainException>(() => AssistantSettings.Create(Guid.NewGuid()).Update(Values(turns: turns)));

    [Theory]
    [InlineData(-1)]
    [InlineData(1441)]
    public void The_cooldown_is_bounded(int cooldown)
        => Assert.Throws<DomainException>(() => AssistantSettings.Create(Guid.NewGuid()).Update(Values(cooldown: cooldown)));

    [Fact]
    public void A_custom_window_needs_both_ends_and_other_modes_drop_them()
    {
        var s = AssistantSettings.Create(Guid.NewGuid());
        Assert.Throws<DomainException>(() => s.Update(Values(hours: AssistantActiveHours.Custom, start: 60)));
        Assert.Throws<DomainException>(() => s.Update(Values(hours: AssistantActiveHours.Custom, start: 60, end: 60)));
        Assert.Throws<DomainException>(() => s.Update(Values(hours: AssistantActiveHours.Custom, start: 60, end: 1440)));
        s.Update(Values(hours: AssistantActiveHours.Custom, start: 18 * 60, end: 8 * 60));
        Assert.Equal(18 * 60, s.CustomStartMinute);
        s.Update(Values(hours: AssistantActiveHours.Always, start: 18 * 60, end: 8 * 60));
        Assert.Null(s.CustomStartMinute);
        Assert.Null(s.CustomEndMinute);
    }

    [Theory]
    [InlineData(19 * 60, true)]
    [InlineData(23 * 60 + 59, true)]
    [InlineData(0, true)]
    [InlineData(7 * 60 + 59, true)]
    [InlineData(8 * 60, false)]
    [InlineData(12 * 60, false)]
    [InlineData(18 * 60, true)]
    public void A_custom_window_may_cross_midnight(int minute, bool inside)
    {
        var s = AssistantSettings.Create(Guid.NewGuid());
        s.Update(Values(hours: AssistantActiveHours.Custom, start: 18 * 60, end: 8 * 60));
        Assert.Equal(inside, s.InCustomWindow(minute));
    }

    [Theory]
    [InlineData(9 * 60, true)]
    [InlineData(17 * 60, false)]
    [InlineData(8 * 60, false)]
    public void A_custom_window_within_a_day(int minute, bool inside)
    {
        var s = AssistantSettings.Create(Guid.NewGuid());
        s.Update(Values(hours: AssistantActiveHours.Custom, start: 9 * 60, end: 17 * 60));
        Assert.Equal(inside, s.InCustomWindow(minute));
    }

    [Theory]
    [InlineData("Visita https://ejemplo.com")]
    [InlineData("Escribe a www.ejemplo.com")]
    [InlineData("Mira ejemplo.com/promo")]
    [InlineData("")]
    public void The_handoff_line_is_required_and_has_no_links(string line)
    {
        var ex = Assert.Throws<DomainException>(() => AssistantSettings.Create(Guid.NewGuid()).Update(Values(handoffLine: line)));
        Assert.Equal(AssistantSettings.HandoffLineRuleMessage, ex.Message);
    }

    [Fact]
    public void Tone_notes_are_bounded_trimmed_and_cleaned_of_control_characters()
    {
        var s = AssistantSettings.Create(Guid.NewGuid());
        Assert.Throws<DomainException>(() => s.Update(Values(tone: new string('a', 501))));
        Assert.Throws<DomainException>(() => s.Update(Values(tone: "Cercano, ver https://x.co")));
        s.Update(Values(tone: "  Cercano\u0007 y​ claro\tsin presión  "));
        Assert.Equal("Cercano y claro sin presión", s.ToneNotes);
        s.Update(Values(tone: "   "));
        Assert.Null(s.ToneNotes);
    }

    [Fact]
    public void Keywords_are_normalized_deduplicated_and_patterns_must_compile()
    {
        var s = AssistantSettings.Create(Guid.NewGuid());
        s.Update(Values(keywords: [" Queja ", "queja", "DEVOLUCIÓN", "re:\\breembols", "  "]));
        Assert.Equal(["queja", "devolucion", "re:\\breembols"], s.HandoffKeywords);
        Assert.Throws<DomainException>(() => s.Update(Values(keywords: ["re:(unclosed"])));
        Assert.Throws<DomainException>(() => s.Update(Values(keywords: ["re:"])));
        Assert.Throws<DomainException>(() => s.Update(Values(keywords: [new string('a', 121)])));
        Assert.Throws<DomainException>(() => s.Update(Values(keywords: Enumerable.Range(0, 51).Select(i => $"k{i}").ToList())));
    }

    [Fact]
    public void Only_colombian_spanish_for_now()
    {
        var ex = Assert.Throws<DomainException>(() => AssistantSettings.Create(Guid.NewGuid()).Update(Values(language: "en-US")));
        Assert.Equal(AssistantSettings.LanguageRuleMessage, ex.Message);
    }

    [Fact]
    public void An_invalid_update_changes_nothing()
    {
        var s = AssistantSettings.Create(Guid.NewGuid());
        Assert.Throws<DomainException>(() => s.Update(Values(delay: 120, turns: 0)));
        Assert.Equal(60, s.ReplyDelaySeconds);
    }

    [Fact]
    public void Enabling_needs_the_terms_and_the_acknowledgement_and_records_who_and_when()
    {
        var s = AssistantSettings.Create(Guid.NewGuid());
        var owner = Guid.NewGuid();
        Assert.Throws<DomainException>(() => s.Enable(owner, "v1", termsAccepted: false, legalBasisAcknowledged: true, T0));
        Assert.Throws<DomainException>(() => s.Enable(owner, "v1", termsAccepted: true, legalBasisAcknowledged: false, T0));
        Assert.Throws<DomainException>(() => s.Enable(owner, " ", termsAccepted: true, legalBasisAcknowledged: true, T0));
        Assert.Throws<DomainException>(() => s.Enable(Guid.Empty, "v1", termsAccepted: true, legalBasisAcknowledged: true, T0));
        Assert.False(s.Enabled);

        Assert.True(s.Enable(owner, "v1", termsAccepted: true, legalBasisAcknowledged: true, T0));
        Assert.True(s.Enabled);
        Assert.Equal("v1", s.TermsVersion);
        Assert.Equal(owner, s.TermsAcceptedBy);
        Assert.Equal(T0, s.TermsAcceptedAt);
        Assert.False(s.Enable(owner, "v1", true, true, T0.AddDays(1)));
        Assert.Equal(T0, s.TermsAcceptedAt);
        Assert.True(s.Enable(owner, "v2", true, true, T0.AddDays(2)));
        Assert.Equal("v2", s.TermsVersion);
    }

    [Fact]
    public void Disabling_keeps_the_consent_evidence()
    {
        var s = AssistantSettings.Create(Guid.NewGuid());
        s.Enable(Guid.NewGuid(), "v1", true, true, T0);
        Assert.True(s.Disable());
        Assert.False(s.Enabled);
        Assert.Equal("v1", s.TermsVersion);
        Assert.False(s.Disable());
    }

    [Theory]
    [InlineData("Queja   por el SERVICIO", "queja por el servicio")]
    [InlineData("Devolución", "devolucion")]
    public void Normalize_lowers_strips_accents_and_collapses_spaces(string text, string expected)
        => Assert.Equal(expected, AssistantSettings.Normalize(text));
}
