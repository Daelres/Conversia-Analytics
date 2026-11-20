using System.Globalization;
using System.Text.Json.Nodes;
using Conversia.Domain.Entities;

namespace Conversia.Application.Assistant.Llm;

/// <summary>
/// One fact of the company's knowledge base as the reply model sees it (JSON with an id) and as the reply checks read it (its quantities, its
/// text). Ids are stable for a published version: <c>service:3</c> is the third service of the list, <c>promo:1</c> the first promotion,
/// <c>faq:2</c>, <c>booking</c>, <c>payment</c>, <c>hours</c> (the conversation's branch) and <c>today</c> (the date and time).
/// </summary>
/// <param name="Title">The promotion's title (its name may be written only when the item is cited); null for other kinds.</param>
public sealed record KnowledgeItem(string Id, string Kind, JsonObject Data, string Text, IReadOnlyList<Quantity> Quantities, string? Title = null)
{
    public const string Service = "service";
    public const string Promotion = "promo";
    public const string Faq = "faq";
    public const string Booking = "booking";
    public const string Payment = "payment";
    public const string Hours = "hours";
    public const string Today = "today";
    public const string Catalog = "catalog";

    /// <summary>For <see cref="Hours"/>: the opening and closing minutes of each weekday (an hour said for a day must be that day's).</summary>
    public IReadOnlyDictionary<DayOfWeek, IReadOnlyList<double>>? ClockByDay { get; init; }

    /// <summary>For <see cref="Hours"/>: the holidays ahead (closed: no hour is that day's).</summary>
    public IReadOnlySet<DateOnly>? ClosedDates { get; init; }

    /// <summary>Estimated tokens of the item's JSON (characters / 3, conservative for Spanish).</summary>
    public int EstimatedTokens => (Data.ToJsonString().Length + 2) / 3;
}

/// <summary>Builds the <see cref="KnowledgeItem"/>s of a knowledge base: the stable ones (cacheable) and today's (date, hours, promotions).</summary>
public static class AssistantKnowledgeItems
{
    private static readonly string[] Weekdays = ["domingo", "lunes", "martes", "miércoles", "jueves", "viernes", "sábado"];

    public static string WeekdayName(DayOfWeek day) => Weekdays[(int)day];

    /// <summary>Services offered at the branch (active only), the frequent questions, the booking policy and the payment methods.</summary>
    public static IReadOnlyList<KnowledgeItem> Stable(AssistantKnowledge knowledge, Guid? branchId)
    {
        ArgumentNullException.ThrowIfNull(knowledge);
        var items = new List<KnowledgeItem>();
        for (var i = 0; i < knowledge.Services.Count; i++)
        {
            var service = knowledge.Services[i];
            if (!service.Active || !OfferedAt(service.BranchIds, branchId))
            {
                continue;
            }

            var data = new JsonObject { ["id"] = $"{KnowledgeItem.Service}:{i + 1}", ["nombre"] = service.Name };
            var quantities = new List<Quantity>();
            if (service.Description is { } description)
            {
                data["descripcion"] = description;
            }

            if (service.PriceMin is { } min)
            {
                if (service.PriceMax is { } max && max != min)
                {
                    data["precio_desde"] = min;
                    data["precio_hasta"] = max;
                    quantities.Add(Price(max));
                }
                else
                {
                    data["precio"] = min;
                }

                quantities.Add(Price(min));
            }

            if (service.DurationMinutes is { } minutes)
            {
                data["duracion_minutos"] = minutes;
                quantities.Add(new Quantity(QuantityKind.Minutes, minutes, null, minutes.ToString(CultureInfo.InvariantCulture)));
                if (minutes % 60 == 0)
                {
                    quantities.Add(new Quantity(QuantityKind.Hours, minutes / 60, null, (minutes / 60).ToString(CultureInfo.InvariantCulture)));
                }
            }

            if (service.Conditions is { } conditions)
            {
                data["condiciones"] = conditions;
            }

            items.Add(Item(data, KnowledgeItem.Service, quantities, service.Name, service.Description, service.Conditions));
        }

        for (var i = 0; i < knowledge.Faqs.Count; i++)
        {
            var faq = knowledge.Faqs[i];
            var data = new JsonObject { ["id"] = $"{KnowledgeItem.Faq}:{i + 1}", ["pregunta"] = faq.Question, ["respuesta"] = faq.Answer };
            if (faq.Topic is { } topic)
            {
                data["tema"] = topic;
            }

            items.Add(Item(data, KnowledgeItem.Faq, [], faq.Question, faq.Answer, faq.Topic));
        }

        if (knowledge.BookingPolicy is { } policy)
        {
            var data = new JsonObject { ["id"] = KnowledgeItem.Booking };
            Set(data, "como_reservar", policy.HowToBook);
            Set(data, "abono", policy.Deposit);
            Set(data, "cancelacion", policy.Cancellation);
            items.Add(Item(data, KnowledgeItem.Booking, [], policy.HowToBook, policy.Deposit, policy.Cancellation));
        }

        if (knowledge.PaymentMethods.Count > 0)
        {
            var data = new JsonObject { ["id"] = KnowledgeItem.Payment, ["medios_de_pago"] = new JsonArray([.. knowledge.PaymentMethods.Select(m => (JsonNode)JsonValue.Create(m)!)]) };
            items.Add(Item(data, KnowledgeItem.Payment, [], [.. knowledge.PaymentMethods]));
        }

        return items;
    }

    /// <summary>Today's facts: the date and time (<c>today</c>), the branch's opening hours and holidays (<c>hours</c>) and the promotions valid today.</summary>
    public static IReadOnlyList<KnowledgeItem> Today(AssistantKnowledge knowledge, Guid? branchId, DateTime localNow)
    {
        ArgumentNullException.ThrowIfNull(knowledge);
        var today = DateOnly.FromDateTime(localNow);
        var minute = (localNow.Hour * 60) + localNow.Minute;
        var open = knowledge.IsOpen(branchId, today, minute);
        var todayData = new JsonObject
        {
            ["id"] = KnowledgeItem.Today,
            ["fecha"] = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["dia"] = WeekdayName(today.DayOfWeek),
            ["hora"] = localNow.ToString("HH:mm", CultureInfo.InvariantCulture),
            ["sede_abierta_ahora"] = open,
        };
        var items = new List<KnowledgeItem>
        {
            new(KnowledgeItem.Today, KnowledgeItem.Today, todayData, string.Empty,
                [DateOf(today), new Quantity(QuantityKind.Count, today.Day, null, today.Day.ToString(CultureInfo.InvariantCulture))]),
        };

        var own = knowledge.OpeningHours.Where(h => h.BranchId is not null && h.BranchId == branchId).ToList();
        var hours = own.Count > 0 ? own : knowledge.OpeningHours.Where(h => h.BranchId is null).ToList();
        var holidays = knowledge.Holidays.Where(h => (h.BranchId is null || h.BranchId == branchId) && h.Date >= today && h.Date <= today.AddDays(30)).OrderBy(h => h.Date).ToList();
        if (hours.Count > 0 || holidays.Count > 0)
        {
            var quantities = new List<Quantity>();
            var week = new JsonArray();
            foreach (var row in hours.OrderBy(h => ((int)h.Day + 6) % 7).ThenBy(h => h.OpensMinute))
            {
                week.Add(new JsonObject { ["dia"] = WeekdayName(row.Day), ["abre"] = Clock(row.OpensMinute), ["cierra"] = Clock(row.ClosesMinute) });
                quantities.Add(ClockFact(row.OpensMinute));
                quantities.Add(ClockFact(row.ClosesMinute));
            }

            var closed = new JsonArray();
            foreach (var holiday in holidays)
            {
                var entry = new JsonObject { ["fecha"] = holiday.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };
                Set(entry, "nota", holiday.Note);
                closed.Add(entry);
                quantities.Add(DateOf(holiday.Date));
            }

            var data = new JsonObject { ["id"] = KnowledgeItem.Hours, ["horario"] = week, ["festivos_proximos_cerrado"] = closed };
            items.Add(new KnowledgeItem(KnowledgeItem.Hours, KnowledgeItem.Hours, data, string.Join(' ', holidays.Select(h => h.Note)), quantities)
            {
                ClockByDay = hours.GroupBy(h => h.Day).ToDictionary(g => g.Key, g => (IReadOnlyList<double>)[.. g.SelectMany(h => new double[] { h.OpensMinute, h.ClosesMinute })]),
                ClosedDates = holidays.Select(h => h.Date).ToHashSet(),
            });
        }

        for (var i = 0; i < knowledge.Promotions.Count; i++)
        {
            var promotion = knowledge.Promotions[i];
            if (promotion.ValidFrom > today || promotion.ValidTo < today || !OfferedAt(promotion.BranchIds, branchId))
            {
                continue;
            }

            var data = new JsonObject
            {
                ["id"] = $"{KnowledgeItem.Promotion}:{i + 1}",
                ["titulo"] = promotion.Title,
                ["valida_desde"] = promotion.ValidFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["valida_hasta"] = promotion.ValidTo.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            };
            Set(data, "descripcion", promotion.Description);
            items.Add(Item(data, KnowledgeItem.Promotion, [DateOf(promotion.ValidFrom), DateOf(promotion.ValidTo)], promotion.Title, promotion.Description) with { Title = promotion.Title });
        }

        return items;
    }

    private static bool OfferedAt(IReadOnlyList<Guid>? branchIds, Guid? branchId)
        => branchIds is null || branchIds.Count == 0 || (branchId is { } id && branchIds.Contains(id));

    private static KnowledgeItem Item(JsonObject data, string kind, List<Quantity> quantities, params string?[] texts)
    {
        var text = string.Join('\n', texts.Where(t => !string.IsNullOrWhiteSpace(t)));
        quantities.AddRange(SpanishQuantities.Read(text));
        return new KnowledgeItem(data["id"]!.GetValue<string>(), kind, data, text, quantities);
    }

    private static void Set(JsonObject data, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            data[name] = value;
        }
    }

    private static Quantity Price(long value) => new(QuantityKind.Price, value, null, value.ToString(CultureInfo.InvariantCulture));

    private static Quantity DateOf(DateOnly date) => new(QuantityKind.Date, (date.Month * 100) + date.Day, null, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    private static Quantity ClockFact(int minute) => new(QuantityKind.Clock, minute, null, Clock(minute));

    private static string Clock(int minute) => $"{minute / 60:00}:{minute % 60:00}";
}

/// <summary>The knowledge items one answer rests on: the stable part (the cached company block) and today's (the user turn).</summary>
/// <param name="Trimmed">The catalog was too big for the budget and only the items the conversation is about were kept.</param>
public sealed record KnowledgeSelection(IReadOnlyList<KnowledgeItem> Stable, IReadOnlyList<KnowledgeItem> Today, bool Trimmed)
{
    public IEnumerable<KnowledgeItem> All => Stable.Concat(Today);

    public KnowledgeItem? Find(string id) => All.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.Ordinal));
}

/// <summary>
/// Chooses what the reply model reads (docs/ASSISTANT.md, "Knowledge selection"). A catalog that fits the token budget goes whole, so the
/// company's block is identical for all its conversations and cached by the provider; a bigger one is cut by a cheap lexical match against
/// the customer's latest messages (words of 4+ letters, folded), keeping the booking policy and the payment methods always.
/// </summary>
public static class AssistantKnowledgeSelector
{
    public const int MaxSelectedServices = 8;
    public const int MaxSelectedFaqs = 6;

    public static KnowledgeSelection Select(AssistantKnowledge knowledge, Guid? branchId, DateTime localNow, IReadOnlyList<string> customerTexts, int tokenBudget)
    {
        ArgumentNullException.ThrowIfNull(knowledge);
        ArgumentNullException.ThrowIfNull(customerTexts);
        var stable = AssistantKnowledgeItems.Stable(knowledge, branchId);
        var today = AssistantKnowledgeItems.Today(knowledge, branchId, localNow);
        if (stable.Sum(i => i.EstimatedTokens) <= tokenBudget)
        {
            return new KnowledgeSelection(stable, today, Trimmed: false);
        }

        var words = Words(string.Join(' ', customerTexts.TakeLast(6)));
        int Score(KnowledgeItem item) => Words(item.Text).Count(words.Contains);
        IEnumerable<KnowledgeItem> Top(string kind, int count) => stable.Where(i => i.Kind == kind)
            .Select((item, index) => (Item: item, Index: index, Score: Score(item)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score).ThenBy(x => x.Index)
            .Take(count)
            .Select(x => x.Item);

        var chosen = Top(KnowledgeItem.Service, MaxSelectedServices).Concat(Top(KnowledgeItem.Faq, MaxSelectedFaqs))
            .Concat(stable.Where(i => i.Kind is KnowledgeItem.Booking or KnowledgeItem.Payment))
            .ToHashSet();
        return new KnowledgeSelection([CatalogIndex(stable), .. stable.Where(chosen.Contains)], today, Trimmed: true);
    }

    /// <summary>The whole catalog by name (ids, names and prices), so a trimmed selection still knows what exists ("¿qué servicios tienen?").</summary>
    private static KnowledgeItem CatalogIndex(IReadOnlyList<KnowledgeItem> stable)
    {
        var services = stable.Where(i => i.Kind == KnowledgeItem.Service).ToList();
        var list = new JsonArray();
        foreach (var service in services)
        {
            var entry = new JsonObject { ["id"] = service.Id, ["nombre"] = service.Data["nombre"]!.DeepClone() };
            foreach (var field in new[] { "precio", "precio_desde", "precio_hasta" })
            {
                if (service.Data[field] is { } value)
                {
                    entry[field] = value.DeepClone();
                }
            }

            list.Add(entry);
        }

        var data = new JsonObject { ["id"] = KnowledgeItem.Catalog, ["servicios"] = list };
        return new KnowledgeItem(KnowledgeItem.Catalog, KnowledgeItem.Catalog, data, string.Join('\n', services.Select(s => s.Data["nombre"]!.GetValue<string>())),
            [.. services.SelectMany(s => s.Quantities.Where(q => q.Kind == QuantityKind.Price)), .. services.SelectMany(s => SpanishQuantities.Read(s.Data["nombre"]!.GetValue<string>()))]);
    }

    private static HashSet<string> Words(string text)
        => [.. AssistantRules.Fold(text).Split((char[])[' ', ',', '.', ';', ':', '?', '¿', '!', '¡', '\n', '(', ')', '/', '-'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 4 && w.All(char.IsLetter)).Select(Stem)];

    // A crude Spanish stem (plural endings): "axilas" and "axila", "piernas" and "pierna" meet.
    private static string Stem(string word) => word.Length > 5 && word.EndsWith("es", StringComparison.Ordinal) ? word[..^2] : word.Length > 4 && word.EndsWith('s') ? word[..^1] : word;
}
