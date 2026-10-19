namespace Conversia.WebhookSimulator.Generation;

/// <summary>
/// Writes the sales chats of a laser hair removal business in the Spanish a Colombian customer and advisor use on WhatsApp. Every chat is
/// built from a random pick of openers, zones, prices, days and hours, so a populated dashboard does not repeat one sentence 300 times, and
/// every kind is written to be read the same way by the rule-based classifier (the unit tests run the real classifier over thousands of them).
/// The customer's words alone never book anything: a Won always carries a message of the advisor, as the product requires (docs/CLASSIFICATION.md).
/// </summary>
public static class ChatFactory
{
    private sealed record Zone(string Words, int PriceThousands, int Sessions);

    private static readonly Zone[] Zones =
    [
        new("axilas", 45, 6),
        new("piernas completas", 120, 6),
        new("bikini", 70, 6),
        new("rostro", 60, 6),
        new("bozo", 35, 4),
        new("brazos", 90, 6),
        new("espalda", 110, 6),
        new("abdomen", 85, 6),
        new("cuerpo completo", 380, 8),
    ];

    private static readonly string[] Days = ["el lunes", "el martes", "el miércoles", "el jueves", "el viernes", "el sábado", "mañana"];

    private static readonly string[] Hours = ["a las 9 am", "a las 10 am", "a las 11 am", "a las 2 pm", "a las 3 pm", "a las 4 pm", "a las 5 pm"];

    private static readonly string[] Emoji = ["", "", "", " 😊", " 🙌", " 🙏", " ✨"];

    /// <summary>The URL-free headline of the ads the click-to-WhatsApp conversations come from.</summary>
    public static readonly string[] AdHeadlines =
    [
        "Depilación láser 2x1 en axilas",
        "Paquete cuerpo completo con descuento",
        "Primera sesión de bikini a mitad de precio",
        "Dile adiós a rasurarte: valoración gratis",
    ];

    public static ChatScript Create(ChatKind kind, Random random, ChatContext context)
    {
        var zone = Zones[random.Next(Zones.Length)];
        var day = Days[random.Next(Days.Length)];
        var hour = Hours[random.Next(Hours.Length)];
        var messages = new List<ScriptedMessage>();
        var clock = 0;

        void Say(Speaker speaker, string text, int gapSeconds)
        {
            clock += gapSeconds;
            messages.Add(new ScriptedMessage(speaker, text, clock));
        }

        void Customer(string text, int minGap, int maxGap) => Say(Speaker.Customer, text + Pick(random, Emoji), random.Next(minGap, maxGap + 1));

        void Business(string text, int minGap, int maxGap) => Say(Speaker.Business, text, random.Next(minGap, maxGap + 1));

        Customer(Opener(random, zone, context), 0, 0);
        if (kind == ChatKind.Unanswered)
        {
            // Nobody answers. Some customers insist once.
            if (random.Next(100) < 35)
            {
                Customer(Pick(random, ["¿Hola? ¿Alguien me atiende?", "Buenas, ¿me pueden responder por favor?", "Hola, ¿me ayudan con la información?"]), 600, 2400);
            }

            return new ChatScript(kind, ExpectedOutcome.InFollowUp, zone.Words, messages);
        }

        var greeting = Pick(random, ["¡Hola", "Hola", "Buenas", "¡Buenas"]);
        var info = $"{greeting} {context.CustomerFirstName}! Con gusto te ayudo. La sesión de {zone.Words} cuesta {zone.PriceThousands} mil pesos y se recomiendan {zone.Sessions} sesiones.";

        switch (kind)
        {
            case ChatKind.Booked:
                Business(info + " ¿Te gustaría agendar una valoración gratis?", context.FirstReplySeconds, context.FirstReplySeconds);
                Customer(Pick(random, ["Me interesa, quiero agendar mi primera sesión", "Sí, quiero agendar la valoración", "Listo, quiero agendar"]), 120, 900);
                Business($"Perfecto. Tenemos cupo {day} {hour}. ¿Te agendo?", 60, 400);
                Customer(Pick(random, ["Dale, agéndame", "Sí, agéndame por favor", "Agéndame ahí"]), 60, 600);
                Business($"Listo, quedó agendada tu cita para {day} {hour}. Te esperamos en la sede {context.BranchName}.", 30, 240);
                if (random.Next(100) < 60)
                {
                    Customer(Pick(random, ["Mil gracias, ahí nos vemos", "Gracias, quedo atenta", "Perfecto, gracias"]), 60, 900);
                }

                return new ChatScript(kind, ExpectedOutcome.Won, zone.Words, messages);

            case ChatKind.BookedAfterPriceObjection:
                Business(info, context.FirstReplySeconds, context.FirstReplySeconds);
                Customer(Pick(random, ["Uf, me parece algo cara, ¿no tienen promoción?", "Está un poco cara, ¿hay algún descuento?", "Me parece cara, ¿tienen alguna oferta?"]), 120, 900);
                Business($"Esta semana tenemos el 2x1: la segunda zona va gratis. Si quieres, te agendo {day} {hour}.", 120, 600);
                Customer(Pick(random, ["Ah bueno, entonces sí, agéndame", "Listo, entonces agéndame", "Así sí, quiero agendar"]), 120, 900);
                Business($"Listo, quedó agendada tu cita para {day} {hour}. Te esperamos en la sede {context.BranchName}.", 30, 240);
                return new ChatScript(kind, ExpectedOutcome.Won, zone.Words, messages);

            case ChatKind.BookedByCustomerYes:
                Business($"{info} ¿Te agendo {day} {hour}?", context.FirstReplySeconds, context.FirstReplySeconds);
                Customer(Pick(random, ["Sí, listo, confirmo", "Dale, confirmo", "Listo, confirmo"]), 60, 900);
                Business("¡Genial! Cualquier cosa nos escribes por aquí.", 30, 240);
                return new ChatScript(kind, ExpectedOutcome.Won, zone.Words, messages);

            case ChatKind.LostPrice:
                Business(info, context.FirstReplySeconds, context.FirstReplySeconds);
                Customer(Pick(random, ["Uf, está muy cara, no me alcanza el presupuesto", "Me parece demasiado cara para mí", "Es muy cara, no me alcanza"]), 120, 900);
                Business("Entiendo. También manejamos un plan de pagos en 3 cuotas sin interés.", 120, 600);
                Customer(Pick(random, ["No gracias, muy amable", "No gracias", "Gracias pero no"]), 120, 1800);
                return new ChatScript(kind, ExpectedOutcome.Lost, zone.Words, messages);

            case ChatKind.LostCompetitor:
                Business(info, context.FirstReplySeconds, context.FirstReplySeconds);
                Customer(Pick(random, ["Gracias, pero ya agendé en otro lado", "Muchas gracias, ya agendé en otra clínica", "Gracias, ya agendé en otro centro"]), 300, 3600);
                return new ChatScript(kind, ExpectedOutcome.Lost, zone.Words, messages);

            case ChatKind.LostNotInterested:
                Business(info, context.FirstReplySeconds, context.FirstReplySeconds);
                Customer(Pick(random, ["No me interesa, gracias", "Gracias, no me interesa", "No estoy interesada, gracias"]), 120, 3600);
                return new ChatScript(kind, ExpectedOutcome.Lost, zone.Words, messages);

            case ChatKind.FollowUpThinking:
                Business(info + " ¿Te gustaría agendar una valoración gratis?", context.FirstReplySeconds, context.FirstReplySeconds);
                Customer(Pick(random, ["Déjame lo pienso y te aviso", "Lo voy a pensar y te cuento", "Tengo que pensarlo, te escribo"]), 120, 1800);
                Business("Claro, sin afán. Aquí estamos cuando quieras.", 60, 600);
                return new ChatScript(kind, ExpectedOutcome.InFollowUp, zone.Words, messages);

            case ChatKind.FollowUpPain:
                Business(info, context.FirstReplySeconds, context.FirstReplySeconds);
                Customer(Pick(random, ["¿Duele mucho? Me da miedo", "Una pregunta, ¿duele?", "¿Y duele el láser?"]), 120, 900);
                Business("Se siente como una pequeña liga sobre la piel; usamos gel frío y el equipo enfría la zona.", 120, 600);
                Customer(Pick(random, ["Gracias por la información", "Ya veo, gracias", "Qué bueno saberlo, gracias"]), 120, 1800);
                return new ChatScript(kind, ExpectedOutcome.InFollowUp, zone.Words, messages);

            case ChatKind.FollowUpSilent:
                Business(info + " ¿Te gustaría agendar una valoración gratis?", context.FirstReplySeconds, context.FirstReplySeconds);
                return new ChatScript(kind, ExpectedOutcome.InFollowUp, zone.Words, messages);

            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }

    private static string Opener(Random random, Zone zone, ChatContext context)
    {
        if (context.FromAd)
        {
            return Pick(random, [$"Hola, vi su anuncio y quiero info de la depilación de {zone.Words}", $"Buenas, vi la promo en Instagram, ¿cuánto vale {zone.Words}?", "Hola, quiero información del 2x1 que vi"]);
        }

        return Pick(random,
        [
            $"Hola, buenas tardes. Quisiera información de la depilación láser en {zone.Words}",
            $"Buenas, ¿cuánto cuesta la depilación de {zone.Words}?",
            $"Hola, me recomendaron su clínica, quiero depilarme {zone.Words}",
            $"Buenos días, estoy cansada de rasurarme, ¿cuánto vale el láser en {zone.Words}?",
            $"Hola, tengo una boda en dos meses y quiero información de {zone.Words}",
            $"Hola, ¿cuánto sale la depilación láser de {zone.Words}?",
        ]);
    }

    private static string Pick(Random random, string[] options) => options[random.Next(options.Length)];
}
