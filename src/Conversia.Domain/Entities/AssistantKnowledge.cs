using Conversia.Domain.Common;
using Conversia.Domain.Enums;
using Conversia.Domain.Exceptions;

namespace Conversia.Domain.Entities;

/// <summary>
/// What the AI sales assistant may tell a customer about one company (docs/ASSISTANT.md, "Knowledge base"): its catalogue, promotions,
/// opening hours and holidays, booking policy, payment methods, frequent questions, the topics it may and may not talk about and when to
/// pass the conversation to a person. Business facts only, never customer data. A value: <see cref="Validated"/> returns the cleaned copy or
/// throws a <see cref="DomainException"/> in Spanish naming the item. Prices are whole Colombian pesos.
/// </summary>
public sealed record AssistantKnowledge
{
    public const int MaxServices = 200;
    public const int MaxPromotions = 50;
    public const int MaxOpeningHours = 500;
    public const int MaxHolidays = 100;
    public const int MaxPaymentMethods = 20;
    public const int MaxFaqs = 200;
    public const int MaxTopics = 50;
    public const int MaxForbiddenPhrases = 100;
    public const int MaxEscalationRules = 30;
    public const int NameMaxLength = 120;
    public const int DescriptionMaxLength = 1000;
    public const int ConditionsMaxLength = 500;
    public const int QuestionMaxLength = 300;
    public const int TopicMaxLength = 60;
    public const int PaymentMethodMaxLength = 80;
    public const int PhraseMaxLength = 120;
    public const int RuleMaxLength = 300;
    public const int NoteMaxLength = 120;
    public const long MaxPrice = 1_000_000_000;
    public const int MaxDurationMinutes = 1440;

    public static readonly string LinkRuleMessage = "Los textos solo pueden enlazar al dominio permitido de reservas o pagos de la empresa.";

    public IReadOnlyList<KnowledgeService> Services { get; init; } = [];

    public IReadOnlyList<KnowledgePromotion> Promotions { get; init; } = [];

    public IReadOnlyList<KnowledgeOpeningHours> OpeningHours { get; init; } = [];

    public IReadOnlyList<KnowledgeHoliday> Holidays { get; init; } = [];

    public KnowledgeBookingPolicy? BookingPolicy { get; init; }

    public IReadOnlyList<string> PaymentMethods { get; init; } = [];

    public IReadOnlyList<KnowledgeFaq> Faqs { get; init; } = [];

    /// <summary>The topics the assistant answers about; a doubt on anything else is passed on, not answered. Empty: the catalogue's own topics.</summary>
    public IReadOnlyList<string> AllowedTopics { get; init; } = [];

    /// <summary>Topics and phrases the assistant never brings up nor writes.</summary>
    public IReadOnlyList<string> ForbiddenPhrases { get; init; } = [];

    /// <summary>When to pass the conversation to a person, in the company's words ("si piden factura electrónica").</summary>
    public IReadOnlyList<string> EscalationRules { get; init; } = [];

    /// <summary>The one domain (and its subdomains) the assistant's texts may link to: the company's booking or payment page.</summary>
    public string? AllowedLinkDomain { get; init; }

    /// <summary>The cleaned, checked copy. <paramref name="branchIds"/> are the company's branches: any other id is refused.</summary>
    public AssistantKnowledge Validated(IReadOnlySet<Guid> branchIds)
    {
        ArgumentNullException.ThrowIfNull(branchIds);
        string? domain = null;
        if (!string.IsNullOrWhiteSpace(AllowedLinkDomain))
        {
            domain = LinkRules.AsciiDomain(AllowedLinkDomain);
            if (AllowedLinkDomain.Trim().Contains("://", StringComparison.Ordinal) || !LinkRules.IsAllowableDomain(domain))
            {
                throw new DomainException("El dominio permitido debe ser solo el nombre del sitio de tu empresa, por ejemplo reservas.miempresa.com (sin https, ruta ni puerto; no se permiten acortadores de enlaces ni sitios compartidos).");
            }
        }

        var checker = new Checker(branchIds, domain);
        return new AssistantKnowledge
        {
            Services = checker.Each(Services, MaxServices, "Servicios", (service, label) =>
            {
                var name = checker.Line(service.Name, NameMaxLength, $"{label}: el nombre", required: true)!;
                if (service.PriceMin is < 0 or > MaxPrice || service.PriceMax is < 0 or > MaxPrice
                    || (service.PriceMax is not null && (service.PriceMin is null || service.PriceMax < service.PriceMin)))
                {
                    throw new DomainException($"{label}: el precio debe ser un valor en pesos entre 0 y {MaxPrice:N0}; en un rango, el mínimo va primero y no supera al máximo.");
                }

                if (service.DurationMinutes is < 1 or > MaxDurationMinutes)
                {
                    throw new DomainException($"{label}: la duración debe estar entre 1 y {MaxDurationMinutes} minutos.");
                }

                return service with
                {
                    Name = name,
                    Description = checker.Text(service.Description, DescriptionMaxLength, $"{label}: la descripción"),
                    Conditions = checker.Text(service.Conditions, ConditionsMaxLength, $"{label}: las condiciones"),
                    BranchIds = checker.BranchList(service.BranchIds, label),
                };
            }, i => $"Servicio {i}"),
            Promotions = checker.Each(Promotions, MaxPromotions, "Promociones", (promotion, label) =>
            {
                if (promotion.ValidTo < promotion.ValidFrom)
                {
                    throw new DomainException($"{label}: la fecha de fin no puede ser anterior a la de inicio.");
                }

                return promotion with
                {
                    Title = checker.Line(promotion.Title, NameMaxLength, $"{label}: el título", required: true)!,
                    Description = checker.Text(promotion.Description, DescriptionMaxLength, $"{label}: la descripción"),
                    BranchIds = checker.BranchList(promotion.BranchIds, label),
                };
            }, i => $"Promoción {i}"),
            OpeningHours = checker.Each(OpeningHours, MaxOpeningHours, "Horarios", (hours, label) =>
            {
                if (!Enum.IsDefined(hours.Day) || hours.OpensMinute is < 0 or >= AssistantSettings.MinutesPerDay
                    || hours.ClosesMinute <= hours.OpensMinute || hours.ClosesMinute > AssistantSettings.MinutesPerDay)
                {
                    throw new DomainException($"{label}: la hora de cierre debe ser posterior a la de apertura, dentro del mismo día.");
                }

                checker.Branch(hours.BranchId, label);
                return hours;
            }, i => $"Horario {i}"),
            Holidays = checker.Each(Holidays, MaxHolidays, "Festivos", (holiday, label) =>
            {
                checker.Branch(holiday.BranchId, label);
                return holiday with { Note = checker.Line(holiday.Note, NoteMaxLength, $"{label}: la nota", required: false) };
            }, i => $"Festivo {i}"),
            BookingPolicy = BookingPolicy is null ? null : Empty(new KnowledgeBookingPolicy(
                checker.Text(BookingPolicy.HowToBook, DescriptionMaxLength, "Cómo reservar"),
                checker.Text(BookingPolicy.Deposit, ConditionsMaxLength, "El abono"),
                checker.Text(BookingPolicy.Cancellation, DescriptionMaxLength, "La cancelación"))),
            PaymentMethods = checker.Words(PaymentMethods, MaxPaymentMethods, PaymentMethodMaxLength, "Medios de pago"),
            Faqs = checker.Each(Faqs, MaxFaqs, "Preguntas frecuentes", (faq, label) => new KnowledgeFaq(
                checker.Line(faq.Question, QuestionMaxLength, $"{label}: la pregunta", required: true)!,
                checker.Text(faq.Answer, DescriptionMaxLength, $"{label}: la respuesta", required: true)!,
                checker.Line(faq.Topic, TopicMaxLength, $"{label}: el tema", required: false)), i => $"Pregunta {i}"),
            AllowedTopics = checker.Words(AllowedTopics, MaxTopics, TopicMaxLength, "Temas permitidos"),
            ForbiddenPhrases = checker.Words(ForbiddenPhrases, MaxForbiddenPhrases, PhraseMaxLength, "Temas y frases prohibidos"),
            EscalationRules = checker.Words(EscalationRules, MaxEscalationRules, RuleMaxLength, "Reglas de traspaso"),
            AllowedLinkDomain = domain,
        };
    }

    /// <summary>
    /// Whether the branch is open at a local date and minute. Null when the knowledge base says nothing about its hours (no row for the
    /// branch and none for every branch). A holiday of the branch, or of every branch, is closed.
    /// </summary>
    public bool? IsOpen(Guid? branchId, DateOnly localDate, int localMinute)
    {
        var own = OpeningHours.Where(h => h.BranchId is not null && h.BranchId == branchId).ToList();
        var hours = own.Count > 0 ? own : OpeningHours.Where(h => h.BranchId is null).ToList();
        if (hours.Count == 0)
        {
            return null;
        }

        if (Holidays.Any(h => h.Date == localDate && (h.BranchId is null || h.BranchId == branchId)))
        {
            return false;
        }

        return hours.Any(h => h.Day == localDate.DayOfWeek && localMinute >= h.OpensMinute && localMinute < h.ClosesMinute);
    }

    private static KnowledgeBookingPolicy? Empty(KnowledgeBookingPolicy policy)
        => policy is { HowToBook: null, Deposit: null, Cancellation: null } ? null : policy;

    private sealed class Checker(IReadOnlySet<Guid> branchIds, string? domain)
    {
        public IReadOnlyList<T> Each<T>(IReadOnlyList<T>? items, int max, string section, Func<T, string, T> check, Func<int, string> label)
        {
            items ??= [];
            if (items.Count > max)
            {
                throw new DomainException($"{section}: a lo sumo {max} elementos.");
            }

            return items.Select((item, i) => item is null ? throw new DomainException($"{label(i + 1)}: está vacío.") : check(item, label(i + 1))).ToList();
        }

        /// <summary>One line: control characters out, spaces collapsed.</summary>
        public string? Line(string? value, int maxLength, string subject, bool required)
            => Checked(value is null ? null : LinkRules.Clean(value), maxLength, subject, required);

        /// <summary>A text that may keep its line breaks.</summary>
        public string? Text(string? value, int maxLength, string subject, bool required = false)
            => Checked(value is null ? null : LinkRules.CleanMultiline(value), maxLength, subject, required);

        public IReadOnlyList<string> Words(IReadOnlyList<string>? values, int max, int maxLength, string section)
        {
            var words = new List<string>();
            foreach (var value in values ?? [])
            {
                var word = Line(value, maxLength, section, required: false);
                if (word is not null && !words.Contains(word, StringComparer.OrdinalIgnoreCase))
                {
                    words.Add(word);
                }
            }

            return words.Count > max ? throw new DomainException($"{section}: a lo sumo {max} elementos.") : words;
        }

        public IReadOnlyList<Guid>? BranchList(IReadOnlyList<Guid>? ids, string label)
        {
            if (ids is null || ids.Count == 0)
            {
                return null;
            }

            foreach (var id in ids)
            {
                Branch(id, label);
            }

            return ids.Distinct().ToList();
        }

        public void Branch(Guid? id, string label)
        {
            if (id is { } branch && !branchIds.Contains(branch))
            {
                throw new DomainException($"{label}: la sucursal no existe en tu empresa.");
            }
        }

        private string? Checked(string? value, int maxLength, string subject, bool required)
        {
            var valid = required ? TextRules.IsValidRequired(value, maxLength) : TextRules.IsValidOptional(value, maxLength);
            if (!valid)
            {
                throw new DomainException($"{subject} {TextRules.RuleText(maxLength, required)}.");
            }

            if (string.IsNullOrEmpty(value))
            {
                return null;
            }

            return LinkRules.OnlyLinksTo(value, domain) ? value : throw new DomainException(LinkRuleMessage);
        }
    }
}

/// <param name="PriceMin">The price, or the lowest of a range, in whole pesos.</param>
/// <param name="PriceMax">The highest of a range; null for a single price.</param>
/// <param name="BranchIds">Where it is offered; empty or null means every branch.</param>
public sealed record KnowledgeService(
    string Name, string? Description, long? PriceMin, long? PriceMax, int? DurationMinutes, string? Conditions, bool Active, IReadOnlyList<Guid>? BranchIds);

public sealed record KnowledgePromotion(string Title, string? Description, DateOnly ValidFrom, DateOnly ValidTo, IReadOnlyList<Guid>? BranchIds);

/// <param name="BranchId">Null: every branch without its own hours.</param>
/// <param name="OpensMinute">Local minute of the day it opens (0 to 1439).</param>
/// <param name="ClosesMinute">Local minute it closes (after it opens, up to 1440).</param>
public sealed record KnowledgeOpeningHours(
    Guid? BranchId, [property: System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<DayOfWeek>))] DayOfWeek Day, int OpensMinute, int ClosesMinute);

public sealed record KnowledgeHoliday(DateOnly Date, Guid? BranchId, string? Note);

public sealed record KnowledgeBookingPolicy(string? HowToBook, string? Deposit, string? Cancellation);

public sealed record KnowledgeFaq(string Question, string Answer, string? Topic);

/// <summary>
/// One saved version of a company's knowledge base. Every change is a new version (a draft); publishing one makes it the version the
/// assistant uses and archives the one before (at most one is published: a partial unique index). The rows are the trail: who saved it
/// (<c>CreatedBy</c>), who published it and when.
/// </summary>
public class AssistantKnowledgeVersion : BaseEntity
{
    /// <summary>The serialized content is bounded too (a defence against a request that passes every per-item limit many times over).</summary>
    public const int ContentMaxLength = 512 * 1024;

    public Guid TenantId { get; private set; }

    /// <summary>1, 2, 3... per company.</summary>
    public int Number { get; private set; }

    public AssistantKnowledgeStatus Status { get; private set; } = AssistantKnowledgeStatus.Draft;

    /// <summary>The validated <see cref="AssistantKnowledge"/> as JSON.</summary>
    public string Content { get; private set; } = "{}";

    public DateTime? PublishedAt { get; private set; }

    public Guid? PublishedBy { get; private set; }

    public static AssistantKnowledgeVersion Create(Guid tenantId, int number, string content, Guid? savedBy)
    {
        if (number < 1)
        {
            throw new DomainException("El número de versión no es válido.");
        }

        if (string.IsNullOrWhiteSpace(content) || content.Length > ContentMaxLength)
        {
            throw new DomainException("La base de conocimiento es demasiado grande.");
        }

        return new AssistantKnowledgeVersion
        {
            TenantId = TextRules.RequiredId(tenantId, "La empresa es obligatoria."),
            Number = number,
            Content = content,
            CreatedBy = savedBy,
        };
    }

    /// <summary>Makes this the version in use. Returns false when it already is.</summary>
    public bool Publish(Guid? userId, DateTime now)
    {
        if (Status == AssistantKnowledgeStatus.Published)
        {
            return false;
        }

        Status = AssistantKnowledgeStatus.Published;
        PublishedBy = userId;
        PublishedAt = UtcTime.Normalize(now);
        return true;
    }

    /// <summary>A newer version was published.</summary>
    public void Archive()
    {
        if (Status == AssistantKnowledgeStatus.Published)
        {
            Status = AssistantKnowledgeStatus.Archived;
        }
    }
}
