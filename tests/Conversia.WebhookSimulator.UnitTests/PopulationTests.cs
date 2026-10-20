using Conversia.Application.Ingestion;
using Conversia.Domain.Enums;
using Conversia.WebhookSimulator.Generation;

namespace Conversia.WebhookSimulator.UnitTests;

public class PopulationTests
{
    private static PopulationOptions Options(int conversations = 300, int days = 30, int seed = 42, double previous = 0.3)
        => new(conversations, days, seed, TimeProvider.System.GetUtcNow().UtcDateTime, previous);

    [Fact]
    public void The_same_seed_and_moment_give_the_same_plan_and_another_seed_a_different_one()
    {
        var options = Options();

        var first = PopulationPlanner.Plan(options);
        var second = PopulationPlanner.Plan(options);
        var other = PopulationPlanner.Plan(options with { Seed = 43 });

        Assert.Equal(first.Count, second.Count);
        Assert.Equal(
            first.Select(c => (c.CustomerWaId, c.BranchName, c.AdvisorName, c.StartedAtUtc, string.Join("|", c.Messages.Select(m => m.Text + m.SentAtUtc.Ticks + m.ProviderId)))),
            second.Select(c => (c.CustomerWaId, c.BranchName, c.AdvisorName, c.StartedAtUtc, string.Join("|", c.Messages.Select(m => m.Text + m.SentAtUtc.Ticks + m.ProviderId)))));
        Assert.NotEqual(first.Select(c => c.CustomerWaId), other.Select(c => c.CustomerWaId));
    }

    [Fact]
    public void The_same_seed_at_another_moment_is_the_same_conversations_with_new_dates()
    {
        var now = TimeProvider.System.GetUtcNow().UtcDateTime;
        var options = new PopulationOptions(200, 30, 42, now);

        var first = PopulationPlanner.Plan(options);
        var later = PopulationPlanner.Plan(options with { Now = now.AddHours(5) });

        Assert.Equal(first.Count, later.Count);
        for (var i = 0; i < first.Count; i++)
        {
            Assert.Equal((first[i].CustomerWaId, first[i].CustomerName, first[i].BranchName, first[i].AdvisorName, first[i].Chat.Kind, first[i].FromAd),
                (later[i].CustomerWaId, later[i].CustomerName, later[i].BranchName, later[i].AdvisorName, later[i].Chat.Kind, later[i].FromAd));
            // A message that is part of both is the same message (same id, same text): a re-run finds it already stored.
            foreach (var (a, b) in first[i].Messages.Zip(later[i].Messages))
            {
                Assert.Equal((a.ProviderId, a.Text), (b.ProviderId, b.Text));
            }
        }

        Assert.Contains(first.Zip(later), pair => pair.First.StartedAtUtc != pair.Second.StartedAtUtc);
    }

    [Fact]
    public void The_plan_has_the_asked_number_of_conversations_with_unique_customers_and_message_ids()
    {
        var plan = PopulationPlanner.Plan(Options(300));

        Assert.Equal(300, plan.Count);
        Assert.Equal(300, plan.Select(c => c.CustomerWaId).Distinct().Count());
        var ids = plan.SelectMany(c => c.Messages).Select(m => m.ProviderId).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(plan, c => Assert.Matches(@"^\d{7,15}$", c.CustomerWaId));
    }

    [Fact]
    public void Nothing_is_dated_in_the_future_and_every_chat_stays_inside_its_window()
    {
        var options = Options();

        var plan = PopulationPlanner.Plan(options);

        var all = plan.SelectMany(c => c.Messages).ToList();
        Assert.All(all, m =>
        {
            Assert.True(m.SentAtUtc <= options.Now.AddSeconds(-30), $"{m.SentAtUtc:O} is too close to now ({options.Now:O}).");
            Assert.True(m.SentAtUtc >= options.Now.AddDays(-2 * options.Days - 1), $"{m.SentAtUtc:O} is older than the history.");
            Assert.True(m.SentAtUtc.Year >= 2015);
        });
        Assert.All(plan, c => Assert.True(c.StartedAtUtc <= options.Now.AddMinutes(-39)));
        Assert.All(plan, c => Assert.Equal(c.Messages.Select(m => m.SentAtUtc).OrderBy(x => x), c.Messages.Select(m => m.SentAtUtc)));
    }

    [Fact]
    public void The_previous_window_gets_its_share_and_the_rest_falls_in_the_recent_window()
    {
        var options = Options(300, 30, previous: 0.3);

        var plan = PopulationPlanner.Plan(options);

        Assert.Equal(90, plan.Count(c => c.IsPrevious));
        Assert.All(plan.Where(c => c.IsPrevious), c => Assert.True(c.StartedAtUtc < options.Now.AddDays(-options.Days)));
        Assert.All(plan.Where(c => !c.IsPrevious), c => Assert.True(c.StartedAtUtc >= options.Now.AddDays(-options.Days).AddMinutes(-21)));
        Assert.Equal(options.Conversations, plan.Count);
    }

    [Fact]
    public void A_few_chats_are_still_waiting_for_an_answer_from_the_last_hours()
    {
        var options = Options();

        var live = PopulationPlanner.Plan(options).Where(c => c.StartedAtUtc > options.Now.AddHours(-7) && c.Chat.Kind == ChatKind.Unanswered).ToList();

        Assert.True(live.Count >= options.LiveUnanswered);
        Assert.All(live, c => Assert.False(c.Chat.HasBusinessReply));
        Assert.All(live, c => Assert.True(options.Now - c.StartedAtUtc > TimeSpan.FromMinutes(30), "past the 30 minute SLA, so it raises the alert"));
    }

    [Fact]
    public void Branches_advisors_origins_and_outcomes_are_spread_with_differences_on_purpose()
    {
        var plan = PopulationPlanner.Plan(Options(1200, previous: 0));

        var byBranch = plan.GroupBy(c => c.BranchName).ToDictionary(g => g.Key, g => g.ToList());
        Assert.Equal(PilotBusiness.Branches.Select(b => b.Name).Order(), byBranch.Keys.Order());
        Assert.True(byBranch["Centro"].Count > byBranch["Poblado"].Count, "Centro has the biggest share");
        double WinRate(string branch) => byBranch[branch].Count(c => c.Chat.Expected == ExpectedOutcome.Won) / (double)byBranch[branch].Count;
        Assert.True(WinRate("Centro") - WinRate("Norte") > 0.2, $"Centro {WinRate("Centro"):P0} vs Norte {WinRate("Norte"):P0}");
        Assert.Contains(plan, c => c.AdvisorName is null);
        Assert.All(plan.Where(c => c.AdvisorName is not null), c => Assert.Contains(PilotBusiness.Advisors, a => a.Name == c.AdvisorName && a.BranchName == c.BranchName));
        Assert.InRange(plan.Count(c => c.FromAd) / (double)plan.Count, 0.3, 0.6);
        Assert.All(plan.Where(c => c.FromAd), c => Assert.NotNull(c.Referral));
        Assert.All(plan.Where(c => !c.FromAd), c => Assert.Null(c.Referral));
        Assert.True(Enum.GetValues<ChatKind>().All(kind => plan.Any(c => c.Chat.Kind == kind)), "every kind of chat appears");
    }

    [Fact]
    public void The_slow_advisor_answers_later_than_the_fast_one()
    {
        var plan = PopulationPlanner.Plan(Options(2400, previous: 0));

        double MedianReply(string advisor)
        {
            var waits = plan.Where(c => c.AdvisorName == advisor && c.Messages.Any(m => m.Speaker == Speaker.Business))
                .Select(c => (c.Messages.First(m => m.Speaker == Speaker.Business).SentAtUtc - c.Messages[0].SentAtUtc).TotalMinutes)
                .Order().ToList();
            return waits[waits.Count / 2];
        }

        Assert.True(MedianReply("Luis Pérez") > 4 * MedianReply("Ana Torres"), $"Luis {MedianReply("Luis Pérez"):0} min vs Ana {MedianReply("Ana Torres"):0} min");
    }

    [Fact]
    public void Conversations_start_when_a_business_gets_them_not_at_three_in_the_morning()
    {
        var plan = PopulationPlanner.Plan(Options(1500, previous: 0));

        var hours = plan.Where(c => c.Chat.Kind != ChatKind.Unanswered || c.StartedAtUtc < TimeProvider.System.GetUtcNow().UtcDateTime.AddHours(-7))
            .Select(c => c.StartedAtUtc.AddHours(PopulationPlanner.LocalOffsetHours).Hour).ToList();

        var night = hours.Count(h => h is >= 0 and <= 5);
        var day = hours.Count(h => h is >= 9 and <= 20);
        Assert.True(night < hours.Count * 0.05, $"{night} of {hours.Count} at night");
        Assert.True(day > hours.Count * 0.8, $"{day} of {hours.Count} during the day");
        Assert.Contains(hours, h => h is >= 10 and <= 12);
        Assert.Contains(hours, h => h is >= 18 and <= 20);
    }

    [Fact]
    public void The_whole_plan_is_accepted_by_the_product_parser_with_nothing_skipped()
    {
        var plan = PopulationPlanner.Plan(Options(120));
        var channel = new ChannelIdentity("573005550101", "106540352242922");

        foreach (var conversation in plan)
        {
            var json = CloudApiPayloads.Conversation(
                channel, conversation.CustomerWaId, conversation.CustomerName,
                conversation.Messages.Where(m => m.Speaker == Speaker.Customer).Select(m => m.ToDelivery()),
                conversation.Messages.Where(m => m.Speaker == Speaker.Business).Select(m => m.ToDelivery()),
                conversation.Referral);

            var parsed = Dialog360PayloadParser.Parse(json);

            Assert.Equal(0, parsed.SkippedCount);
            Assert.Equal(conversation.Messages.Count, parsed.Messages.Count);
        }
    }

    [Fact]
    public void Every_hour_and_day_weight_is_between_zero_and_one()
    {
        for (var hour = 0; hour < 24; hour++)
        {
            Assert.InRange(PopulationPlanner.HourWeight(hour), 0.01, 1.0);
        }

        foreach (var day in Enum.GetValues<DayOfWeek>())
        {
            Assert.InRange(PopulationPlanner.DayWeight(day), 0.01, 1.0);
        }
    }

    [Fact]
    public void Each_branch_of_the_pilot_has_two_advisors_and_a_number_of_its_own()
    {
        Assert.All(PilotBusiness.Branches, b => Assert.Equal(2, PilotBusiness.Advisors.Count(a => a.BranchName == b.Name)));
        Assert.Equal(PilotBusiness.Branches.Count, Enumerable.Range(0, PilotBusiness.Branches.Count).Select(PilotBusiness.DisplayNumber).Distinct().Count());
        Assert.Equal(1.0, PilotBusiness.Branches.Sum(b => b.Weight), 6);
    }
}
