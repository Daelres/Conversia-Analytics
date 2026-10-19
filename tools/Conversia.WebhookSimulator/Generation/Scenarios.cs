namespace Conversia.WebhookSimulator.Generation;

/// <param name="Description">What is being sent, for the console.</param>
/// <param name="Json">The body of the POST.</param>
/// <param name="Expected">What the API should do with it, for the console.</param>
public sealed record PlannedDelivery(string Description, string Json, string Expected);

/// <param name="Now">The instant everything is relative to: nothing the scenarios send is later than this.</param>
public sealed record ScenarioContext(ChannelIdentity Channel, string BranchName, DateTime Now, int Seed);

/// <summary>The scripted scenarios of <c>simulator play</c>: small, readable cases for trying the pipeline by hand.</summary>
public static class Scenarios
{
    public const string All = "all";

    public static readonly string[] Names = ["booking", "price-objection", "unanswered", "ad-origin", "bsuid", "duplicate", "out-of-order"];

    public static bool IsKnown(string name) => name == All || Names.Contains(name);

    public static IReadOnlyList<PlannedDelivery> Build(string name, ScenarioContext context)
    {
        if (name == All)
        {
            return Names.SelectMany(scenario => Build(scenario, context with { Seed = context.Seed + Array.IndexOf(Names, scenario) * 101 })).ToList();
        }

        var random = new Random(context.Seed);
        var taken = new HashSet<string>();
        var firstName = Customers.FirstName(random);
        var profile = Customers.ProfileName(random, firstName);
        var waId = Customers.WaId(random, taken);
        var runKey = $"{context.Seed:X}{name[..2].ToUpperInvariant()}";
        var chatContext = new ChatContext(firstName, context.BranchName, FirstReplySeconds: 120);

        return name switch
        {
            "booking" => Chat(ChatKind.Booked, "A customer asks about a zone, books and the advisor confirms",
                "Won, once the classifier has read it (about a minute of quiet)", context, chatContext, random, runKey, profile, waId, referral: null),
            "price-objection" => Chat(ChatKind.LostPrice, "A customer finds it too expensive and says no",
                "Lost (price)", context, chatContext, random, runKey, profile, waId, referral: null),
            "unanswered" => Chat(ChatKind.Unanswered, "A customer writes and nobody answers (45 minutes ago, over the 30 minute SLA)",
                "An open unanswered conversation: the dashboard alert and the 'Sin responder' card", context, chatContext, random, runKey, profile, waId, referral: null, startedMinutesAgo: 45),
            "ad-origin" => Chat(ChatKind.BookedByCustomerYes, "A customer arrives from a click-to-WhatsApp ad and books",
                "Source 'Ads' (the referral), then Won", context, chatContext with { FromAd = true }, random, runKey, profile, waId, AdFor(random)),
            "bsuid" => BusinessScopedUser(context, random, runKey, profile),
            "duplicate" => Duplicate(context, chatContext, random, runKey, profile, waId),
            "out-of-order" => OutOfOrder(context, chatContext, random, runKey, profile, waId),
            _ => throw new ArgumentException($"Unknown scenario '{name}'. Use one of: {string.Join(", ", Names)}, {All}.", nameof(name)),
        };
    }

    private static AdReferral AdFor(Random random)
    {
        var id = random.Next(100_000, 999_999);
        return new AdReferral($"https://fb.me/sim-ad-{id}", $"1202000000{id}", ChatFactory.AdHeadlines[random.Next(ChatFactory.AdHeadlines.Length)]);
    }

    // A chat told message by message, as the provider sends them: the customer's in the messages field, the business' as echoes.
    private static IReadOnlyList<PlannedDelivery> Chat(
        ChatKind kind, string description, string expected, ScenarioContext context, ChatContext chatContext, Random random, string runKey, string profile, string waId,
        AdReferral? referral, int? startedMinutesAgo = null)
    {
        var chat = ChatFactory.Create(kind, random, chatContext);
        var duration = chat.Messages[^1].OffsetSeconds;
        var start = context.Now.AddMinutes(-(startedMinutesAgo ?? 3)).AddSeconds(-(startedMinutesAgo is null ? duration : 0));
        var placed = ChatTimeline.Place(chat, start, context.Now.AddSeconds(-10), runKey, 1);
        var deliveries = new List<PlannedDelivery>();
        foreach (var (message, index) in placed.Select((m, i) => (m, i)))
        {
            var body = message.Speaker == Speaker.Customer
                ? CloudApiPayloads.Inbound(context.Channel, waId, profile, [message.ToDelivery()], index == 0 ? referral : null)
                : CloudApiPayloads.Echo(context.Channel, waId, [message.ToDelivery()]);
            var who = message.Speaker == Speaker.Customer ? "customer" : "business echo";
            deliveries.Add(new PlannedDelivery($"{description}: message {index + 1}/{placed.Count} ({who})", body, index == placed.Count - 1 ? expected : "stored"));
        }

        return deliveries;
    }

    private static IReadOnlyList<PlannedDelivery> BusinessScopedUser(ScenarioContext context, Random random, string runKey, string profile)
    {
        var userId = "CO." + random.NextInt64(1_000_000_000_000_000_000, 9_000_000_000_000_000_000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var message = new DeliveryMessage(ChatTimeline.MessageId(runKey, 1, 0), "Hola, quiero información de la depilación láser", CloudApiPayloads.ToUnix(context.Now.AddMinutes(-5)));
        return
        [
            new PlannedDelivery(
                "A customer identified by a business-scoped user id, with no phone number",
                CloudApiPayloads.UserWithoutPhone(context.Channel, userId, profile, message),
                "Answered 200 but skipped and counted (SkippedMessageCount): no contact and no conversation is created"),
        ];
    }

    private static IReadOnlyList<PlannedDelivery> Duplicate(ScenarioContext context, ChatContext chatContext, Random random, string runKey, string profile, string waId)
    {
        var chat = ChatFactory.Create(ChatKind.FollowUpSilent, random, chatContext);
        var message = ChatTimeline.Place(chat, context.Now.AddMinutes(-20), context.Now, runKey, 1)[0];
        var body = CloudApiPayloads.Inbound(context.Channel, waId, profile, [message.ToDelivery()]);
        return
        [
            new PlannedDelivery("A customer message", body, "Stored: one conversation with one message"),
            new PlannedDelivery("The very same body again (the provider retries)", body, "200, nothing stored (same body hash on the channel)"),
            new PlannedDelivery("The same message inside another body", CloudApiPayloads.WithExtraField(body), "200 and stored as an event, but the message is skipped (same provider message id)"),
        ];
    }

    private static IReadOnlyList<PlannedDelivery> OutOfOrder(ScenarioContext context, ChatContext chatContext, Random random, string runKey, string profile, string waId)
    {
        var start = context.Now.AddMinutes(-30);
        var first = new DeliveryMessage(ChatTimeline.MessageId(runKey, 1, 0), "Hola, buenas tardes. ¿Cuánto cuesta la depilación de axilas?", CloudApiPayloads.ToUnix(start));
        var reply = new DeliveryMessage(ChatTimeline.MessageId(runKey, 1, 1), $"¡Hola {chatContext.CustomerFirstName}! La sesión de axilas cuesta 45 mil pesos.", CloudApiPayloads.ToUnix(start.AddMinutes(2)));
        var second = new DeliveryMessage(ChatTimeline.MessageId(runKey, 1, 2), "Gracias, lo reviso", CloudApiPayloads.ToUnix(start.AddMinutes(5)));
        _ = random;
        return
        [
            new PlannedDelivery("The customer's LAST message arrives first", CloudApiPayloads.Inbound(context.Channel, waId, profile, [second]), "Stored: it opens the conversation"),
            new PlannedDelivery("The advisor's reply arrives second (written between the two)", CloudApiPayloads.Echo(context.Channel, waId, [reply]), "Stored as the first response"),
            new PlannedDelivery(
                "The customer's FIRST message arrives last",
                CloudApiPayloads.Inbound(context.Channel, waId, profile, [first]),
                "Stored: the conversation times are recomputed from the message times, so the response time is 2 minutes, not negative"),
        ];
    }
}
