namespace Conversia.WebhookSimulator.Generation;

/// <summary>An advisor of the business and how fast they usually answer.</summary>
/// <param name="MedianFirstReplyMinutes">Half of their first answers come sooner than this.</param>
public sealed record AdvisorProfile(string Name, string BranchName, double MedianFirstReplyMinutes);

/// <summary>How a branch does: its share of the conversations, how many customers it wins, and how often nobody answers.</summary>
/// <param name="WinRate">Share of the answered conversations that end booked (0 to 1).</param>
public sealed record BranchProfile(string Name, double Weight, double WinRate, double AdsShare, double UnansweredShare);

/// <summary>The people and places the simulator invents for the pilot (a laser hair removal business with four branches).</summary>
public static class PilotBusiness
{
    public static readonly IReadOnlyList<BranchProfile> Branches =
    [
        new("Centro", 0.34, 0.46, 0.45, 0.04),
        new("Norte", 0.26, 0.16, 0.35, 0.12),
        new("Sur", 0.22, 0.34, 0.40, 0.07),
        new("Poblado", 0.18, 0.40, 0.55, 0.05),
    ];

    public static readonly IReadOnlyList<AdvisorProfile> Advisors =
    [
        new("Ana Torres", "Centro", 4),
        new("Luis Pérez", "Centro", 55),
        new("Marta Gil", "Norte", 12),
        new("Carlos Ríos", "Norte", 70),
        new("Paola Mejía", "Sur", 8),
        new("Jorge Salazar", "Sur", 28),
        new("Daniela Arango", "Poblado", 6),
        new("Felipe Cardona", "Poblado", 35),
    ];

    /// <summary>The number of the business at a branch, as the simulator registers it (+57 300 555 01NN).</summary>
    public static string DisplayNumber(int branchIndex) => $"+57 300 555 {101 + branchIndex:D4}";
}

/// <param name="Conversations">How many conversations in all.</param>
/// <param name="Days">The length of the window the main share of them falls in; the window ends now.</param>
/// <param name="PreviousShare">The share of <paramref name="Conversations"/> placed in the window right before (same length), where the business did better: it gives the dashboard a trend to show.</param>
/// <param name="LiveUnanswered">Conversations opened in the last hours that nobody has answered: the ones the unanswered alert is about.</param>
public sealed record PopulationOptions(int Conversations, int Days, int Seed, DateTime Now, double PreviousShare = 0.3, int LiveUnanswered = 5);

/// <summary>One invented conversation, ready to be sent: who, where, with whom, when, and what was said.</summary>
public sealed record PlannedConversation(
    int Index,
    string CustomerWaId,
    string CustomerName,
    string BranchName,
    string? AdvisorName,
    bool FromAd,
    AdReferral? Referral,
    ChatScript Chat,
    DateTime StartedAtUtc,
    IReadOnlyList<TimedMessage> Messages,
    bool IsPrevious);

/// <summary>
/// Decides a believable spread of conversations over branches, advisors, origins, outcomes, days and hours of the week, with differences on
/// purpose (one branch that wins little, one advisor who answers slowly, a few chats still waiting for an answer, a worse period behind) so the
/// dashboard has something to say. The same seed and the same "now" always give the same plan.
/// </summary>
public static class PopulationPlanner
{
    /// <summary>Bogota has no daylight saving: local time is UTC-5 all year (the default <c>Metrics:TimeZone</c>).</summary>
    public const int LocalOffsetHours = -5;

    private const double PreviousWinBoost = 0.16;

    public static IReadOnlyList<PlannedConversation> Plan(PopulationOptions options)
    {
        var runKey = options.Seed.ToString("X", System.Globalization.CultureInfo.InvariantCulture);
        var taken = new HashSet<string>();
        var previousCount = (int)Math.Round(options.Conversations * Math.Clamp(options.PreviousShare, 0, 0.6));
        var live = Math.Min(options.LiveUnanswered, Math.Max(0, options.Conversations - previousCount));
        var recentCount = options.Conversations - previousCount - live;

        var plan = new List<PlannedConversation>(options.Conversations);
        var windowEnd = options.Now.AddMinutes(-20);
        for (var i = 0; i < recentCount; i++)
        {
            plan.Add(PlanOne(plan.Count, options, runKey, taken, previous: false, live: false, windowEnd.AddDays(-options.Days), windowEnd));
        }

        for (var i = 0; i < previousCount; i++)
        {
            plan.Add(PlanOne(plan.Count, options, runKey, taken, previous: true, live: false, windowEnd.AddDays(-2 * options.Days), windowEnd.AddDays(-options.Days)));
        }

        for (var i = 0; i < live; i++)
        {
            plan.Add(PlanOne(plan.Count, options, runKey, taken, previous: false, live: true, options.Now.AddHours(-6), options.Now.AddMinutes(-40)));
        }

        return plan;
    }

    // Who a conversation is, and what is said in it, comes from a stream of random numbers of its own (seed and position), and WHEN it started from another:
    // the same seed run at another moment is the same customers, branches, advisors and message ids with new dates. That is what makes running populate twice harmless:
    // the messages are already there, whatever time it is.
    private static PlannedConversation PlanOne(
        int index, PopulationOptions options, string runKey, HashSet<string> taken, bool previous, bool live, DateTime from, DateTime to)
    {
        var random = Stream(options.Seed, index, salt: 1);
        var timing = Stream(options.Seed, index, salt: 2);
        var branch = PickBranch(random);
        var advisorProfile = PickAdvisor(random, branch.Name);
        var assigned = advisorProfile is not null && random.NextDouble() < 0.88;
        var fromAd = random.NextDouble() < branch.AdsShare;
        var firstName = Customers.FirstName(random);
        var waId = Customers.WaId(random, taken);
        var profileName = Customers.ProfileName(random, firstName);

        var median = (assigned ? advisorProfile!.MedianFirstReplyMinutes : 25) * (previous ? 0.7 : 1);
        var firstReplySeconds = (int)Math.Clamp(median * 60 * Math.Exp(Gaussian(random) * 0.6), 30, 6 * 3600);

        var kind = live ? ChatKind.Unanswered : PickKind(random, branch, previous);
        var context = new ChatContext(firstName, branch.Name, firstReplySeconds, fromAd);
        var chat = ChatFactory.Create(kind, random, context);
        var start = live ? from.AddSeconds(timing.NextDouble() * (to - from).TotalSeconds) : PickStart(timing, from, to);
        var messages = ChatTimeline.Place(chat, start, options.Now.AddSeconds(-30), runKey, index);
        var referral = fromAd ? new AdReferral($"https://fb.me/sim-ad-{index:D4}", $"1202000{index:D6}", ChatFactory.AdHeadlines[random.Next(ChatFactory.AdHeadlines.Length)]) : null;

        return new PlannedConversation(index, waId, profileName, branch.Name, assigned ? advisorProfile!.Name : null, fromAd, referral, chat, start, messages, previous);
    }

    /// <summary>A stream of random numbers that depends only on the seed, the position of the conversation and a salt (a stable mix: no per-process hashing).</summary>
    private static Random Stream(int seed, int index, int salt)
    {
        unchecked
        {
            var h = (uint)seed * 2654435761u;
            h ^= ((uint)index * 40503u) + ((uint)salt * 9973u);
            h *= 2246822519u;
            h ^= h >> 15;
            h *= 3266489917u;
            h ^= h >> 13;
            return new Random((int)h);
        }
    }

    private static BranchProfile PickBranch(Random random)
    {
        var roll = random.NextDouble() * PilotBusiness.Branches.Sum(b => b.Weight);
        foreach (var branch in PilotBusiness.Branches)
        {
            roll -= branch.Weight;
            if (roll <= 0)
            {
                return branch;
            }
        }

        return PilotBusiness.Branches[^1];
    }

    // The first advisor of a branch takes 60 % of its chats.
    private static AdvisorProfile? PickAdvisor(Random random, string branchName)
    {
        var advisors = PilotBusiness.Advisors.Where(a => a.BranchName == branchName).ToList();
        return advisors.Count == 0 ? null : advisors[random.NextDouble() < 0.6 ? 0 : advisors.Count - 1];
    }

    private static ChatKind PickKind(Random random, BranchProfile branch, bool previous)
    {
        if (random.NextDouble() < branch.UnansweredShare * (previous ? 0.5 : 1))
        {
            return ChatKind.Unanswered;
        }

        var winRate = Math.Min(0.8, branch.WinRate + (previous ? PreviousWinBoost : 0));
        if (random.NextDouble() < winRate)
        {
            var roll = random.NextDouble();
            return roll < 0.5 ? ChatKind.Booked : roll < 0.75 ? ChatKind.BookedAfterPriceObjection : ChatKind.BookedByCustomerYes;
        }

        if (random.NextDouble() < 0.42)
        {
            var roll = random.NextDouble();
            return roll < 0.55 ? ChatKind.LostPrice : roll < 0.75 ? ChatKind.LostCompetitor : ChatKind.LostNotInterested;
        }

        var follow = random.NextDouble();
        return follow < 0.4 ? ChatKind.FollowUpThinking : follow < 0.6 ? ChatKind.FollowUpPain : ChatKind.FollowUpSilent;
    }

    // A day of the window and an hour of that day in local time, weighted like a business with ads: busy at midday and after work, quiet at night and on Sundays.
    private static DateTime PickStart(Random random, DateTime from, DateTime to)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var candidate = from.AddSeconds(random.NextDouble() * (to - from).TotalSeconds);
            var local = candidate.AddHours(LocalOffsetHours);
            if (random.NextDouble() < HourWeight(local.Hour) * DayWeight(local.DayOfWeek))
            {
                return candidate;
            }
        }

        return from.AddSeconds((to - from).TotalSeconds / 2);
    }

    /// <summary>How busy a local hour is, 0 to 1.</summary>
    public static double HourWeight(int hour) => hour switch
    {
        >= 10 and <= 12 => 1.0,
        >= 18 and <= 20 => 1.0,
        >= 15 and <= 17 => 0.8,
        13 or 14 => 0.6,
        8 or 9 => 0.5,
        21 or 22 => 0.45,
        7 or 23 => 0.15,
        _ => 0.04,
    };

    public static double DayWeight(DayOfWeek day) => day switch
    {
        DayOfWeek.Sunday => 0.35,
        DayOfWeek.Saturday => 0.8,
        _ => 1.0,
    };

    private static double Gaussian(Random random)
    {
        var u1 = 1.0 - random.NextDouble();
        var u2 = random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
