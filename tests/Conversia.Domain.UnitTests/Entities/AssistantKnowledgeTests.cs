using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Domain.Exceptions;

namespace Conversia.Domain.UnitTests.Entities;

public class AssistantKnowledgeTests
{
    private static readonly Guid BranchA = Guid.NewGuid();
    private static readonly Guid BranchB = Guid.NewGuid();
    private static readonly IReadOnlySet<Guid> Branches = new HashSet<Guid> { BranchA, BranchB };

    private static KnowledgeService Service(
        string name = "Depilación láser piernas", long? min = 120_000, long? max = null, int? duration = 45, IReadOnlyList<Guid>? branches = null,
        string? description = "Sesión con equipo de diodo.", string? conditions = null)
        => new(name, description, min, max, duration, conditions, true, branches);

    private static AssistantKnowledge With(params KnowledgeService[] services) => new() { Services = services };

    [Fact]
    public void A_valid_base_is_kept_and_cleaned()
    {
        var knowledge = new AssistantKnowledge
        {
            Services = [Service(name: "  Depilación   láser\tpiernas ", branches: [BranchA])],
            PaymentMethods = [" Nequi ", "Tarjeta", "nequi"],
            Faqs = [new KnowledgeFaq("¿Duele?", "Se siente un leve calor.\nNada más.", " procedimiento ")],
            AllowedTopics = ["depilación", "precios"],
            AllowedLinkDomain = " Reservas.Ejemplo.com ",
        };

        var valid = knowledge.Validated(Branches);

        Assert.Equal("Depilación láser piernas", valid.Services[0].Name);
        Assert.Equal(["Nequi", "Tarjeta"], valid.PaymentMethods);
        Assert.Equal("Se siente un leve calor.\nNada más.", valid.Faqs[0].Answer);
        Assert.Equal("procedimiento", valid.Faqs[0].Topic);
        Assert.Equal("reservas.ejemplo.com", valid.AllowedLinkDomain);
    }

    [Fact]
    public void A_service_needs_a_name_within_its_length()
    {
        var ex = Assert.Throws<DomainException>(() => With(Service(name: " ")).Validated(Branches));
        Assert.Contains("Servicio 1", ex.Message);
        Assert.Throws<DomainException>(() => With(Service(name: new string('a', 121))).Validated(Branches));
    }

    [Theory]
    [InlineData(-1L, null)]
    [InlineData(150_000L, 100_000L)]
    [InlineData(null, 100_000L)]
    [InlineData(1_000_000_001L, null)]
    public void Prices_are_whole_pesos_and_a_range_goes_up(long? min, long? max)
    {
        var ex = Assert.Throws<DomainException>(() => With(Service(min: min, max: max)).Validated(Branches));
        Assert.Contains("precio", ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1441)]
    public void A_duration_is_between_one_minute_and_a_day(int duration)
        => Assert.Throws<DomainException>(() => With(Service(duration: duration)).Validated(Branches));

    [Fact]
    public void A_branch_of_another_company_is_refused()
    {
        var ex = Assert.Throws<DomainException>(() => With(Service(branches: [Guid.NewGuid()])).Validated(Branches));
        Assert.Contains("sucursal", ex.Message);
    }

    [Fact]
    public void Links_go_only_to_the_allowed_domain()
    {
        var linked = With(Service(description: "Reserva en https://reservas.ejemplo.com/agenda o en citas.reservas.ejemplo.com"));
        var ex = Assert.Throws<DomainException>(() => linked.Validated(Branches));
        Assert.Equal(AssistantKnowledge.LinkRuleMessage, ex.Message);

        var allowed = linked with { AllowedLinkDomain = "reservas.ejemplo.com" };
        allowed.Validated(Branches);

        var other = allowed with { Faqs = [new KnowledgeFaq("¿Dónde pago?", "Paga en www.otro-sitio.com", null)] };
        Assert.Throws<DomainException>(() => other.Validated(Branches));
        var lookalike = allowed with { Faqs = [new KnowledgeFaq("¿Dónde pago?", "Paga en falsoreservas.ejemplo.com.co", null)] };
        Assert.Throws<DomainException>(() => lookalike.Validated(Branches));
    }

    [Theory]
    [InlineData("https://reservas.ejemplo.com")]
    [InlineData("reservas")]
    [InlineData("reservas.ejemplo.com/agenda")]
    [InlineData("reservas.ejemplo.com:8080")]
    [InlineData("bit.ly")]
    [InlineData("mi-tienda.github.io")]
    [InlineData("com.co")]
    public void The_allowed_domain_is_a_host_name(string domain)
    {
        var ex = Assert.Throws<DomainException>(() => new AssistantKnowledge { AllowedLinkDomain = domain }.Validated(Branches));
        Assert.Contains("dominio", ex.Message);
    }

    [Fact]
    public void Promotions_need_their_dates_in_order()
    {
        var promotion = new KnowledgePromotion("2x1 en axilas", null, new DateOnly(2026, 12, 1), new DateOnly(2026, 11, 1), null);
        var ex = Assert.Throws<DomainException>(() => new AssistantKnowledge { Promotions = [promotion] }.Validated(Branches));
        Assert.Contains("Promoción 1", ex.Message);
    }

    [Theory]
    [InlineData(-1, 600)]
    [InlineData(600, 600)]
    [InlineData(600, 1441)]
    [InlineData(900, 600)]
    public void Opening_hours_close_after_they_open_within_the_day(int opens, int closes)
    {
        var hours = new KnowledgeOpeningHours(BranchA, DayOfWeek.Monday, opens, closes);
        Assert.Throws<DomainException>(() => new AssistantKnowledge { OpeningHours = [hours] }.Validated(Branches));
    }

    [Fact]
    public void Lists_are_bounded()
    {
        var many = Enumerable.Range(0, AssistantKnowledge.MaxServices + 1).Select(i => Service(name: $"Servicio {i}")).ToArray();
        Assert.Throws<DomainException>(() => With(many).Validated(Branches));
        Assert.Throws<DomainException>(() => new AssistantKnowledge { AllowedTopics = Enumerable.Range(0, 51).Select(i => $"t{i}").ToList() }.Validated(Branches));
    }

    [Fact]
    public void Control_and_invisible_characters_are_dropped()
        => Assert.Equal("Láser axilas", With(Service(name: "Láser\0 \u200baxilas\u0007")).Validated(Branches).Services[0].Name);

    [Fact]
    public void Hours_say_open_closed_or_unknown_and_holidays_close()
    {
        var monday = new DateOnly(2026, 11, 23);
        var knowledge = new AssistantKnowledge
        {
            OpeningHours =
            [
                new KnowledgeOpeningHours(null, DayOfWeek.Monday, 8 * 60, 18 * 60),
                new KnowledgeOpeningHours(BranchB, DayOfWeek.Monday, 10 * 60, 12 * 60),
                new KnowledgeOpeningHours(BranchB, DayOfWeek.Monday, 14 * 60, 16 * 60),
            ],
            Holidays = [new KnowledgeHoliday(new DateOnly(2026, 12, 8), null, "Inmaculada Concepción")],
        };

        Assert.True(knowledge.IsOpen(BranchA, monday, 9 * 60)); // the hours of every branch
        Assert.False(knowledge.IsOpen(BranchA, monday, 18 * 60));
        Assert.False(knowledge.IsOpen(BranchB, monday, 9 * 60)); // its own hours win
        Assert.True(knowledge.IsOpen(BranchB, monday, 15 * 60));
        Assert.False(knowledge.IsOpen(BranchA, monday.AddDays(1), 9 * 60)); // Tuesday: no hours that day
        Assert.False(knowledge.IsOpen(BranchA, new DateOnly(2026, 12, 7).AddDays(1), 9 * 60)); // the holiday (a Tuesday anyway)
        Assert.Null(new AssistantKnowledge().IsOpen(BranchA, monday, 9 * 60));
    }

    [Fact]
    public void A_holiday_on_a_working_day_is_closed()
    {
        var monday = new DateOnly(2026, 11, 23);
        var knowledge = new AssistantKnowledge
        {
            OpeningHours = [new KnowledgeOpeningHours(null, DayOfWeek.Monday, 8 * 60, 18 * 60)],
            Holidays = [new KnowledgeHoliday(monday, BranchA, null)],
        };
        Assert.False(knowledge.IsOpen(BranchA, monday, 9 * 60));
        Assert.True(knowledge.IsOpen(BranchB, monday, 9 * 60));
    }

    [Fact]
    public void Versions_publish_once_and_archive()
    {
        var version = AssistantKnowledgeVersion.Create(Guid.NewGuid(), 1, "{}", Guid.NewGuid());
        Assert.Equal(AssistantKnowledgeStatus.Draft, version.Status);
        var at = new DateTime(2026, 11, 19, 15, 0, 0, DateTimeKind.Utc);
        Assert.True(version.Publish(Guid.NewGuid(), at));
        Assert.Equal(at, version.PublishedAt);
        Assert.False(version.Publish(Guid.NewGuid(), at.AddHours(1)));
        version.Archive();
        Assert.Equal(AssistantKnowledgeStatus.Archived, version.Status);
        Assert.Throws<DomainException>(() => AssistantKnowledgeVersion.Create(Guid.NewGuid(), 0, "{}", null));
        Assert.Throws<DomainException>(() => AssistantKnowledgeVersion.Create(Guid.NewGuid(), 1, new string('x', AssistantKnowledgeVersion.ContentMaxLength + 1), null));
    }
}
