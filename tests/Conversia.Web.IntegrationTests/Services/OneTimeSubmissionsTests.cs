using Conversia.Web.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Conversia.Web.IntegrationTests.Services;

public class OneTimeSubmissionsTests
{
    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan span) => _now += span;
    }

    private static string Id(int n) => n.ToString("x32");

    [Fact]
    public void A_fresh_id_is_claimed_once_and_a_replay_is_refused()
    {
        var submissions = new OneTimeSubmissions();
        var id = OneTimeSubmissions.NewId();

        Assert.True(submissions.TryClaim(id));
        Assert.False(submissions.TryClaim(id));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void A_blank_or_foreign_id_is_never_accepted(string? id)
    {
        Assert.False(new OneTimeSubmissions().TryClaim(id));
    }

    [Fact]
    public void A_claimed_id_is_forgotten_after_fifteen_minutes()
    {
        var clock = new ManualClock();
        var submissions = new OneTimeSubmissions(clock);

        Assert.True(submissions.TryClaim(Id(1)));
        clock.Advance(OneTimeSubmissions.Lifetime - TimeSpan.FromSeconds(1));
        Assert.False(submissions.TryClaim(Id(1)));
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(submissions.TryClaim(Id(1)));
    }

    [Fact]
    public void The_memory_is_bounded_and_the_oldest_id_is_the_first_to_be_forgotten()
    {
        var submissions = new OneTimeSubmissions(capacity: 3);

        for (var n = 1; n <= 3; n++)
        {
            Assert.True(submissions.TryClaim(Id(n)));
        }

        // A fourth id pushes the first one out; the newer ones are still remembered.
        Assert.True(submissions.TryClaim(Id(4)));
        Assert.False(submissions.TryClaim(Id(4)));
        Assert.False(submissions.TryClaim(Id(3)));
        Assert.True(submissions.TryClaim(Id(1)));
    }

    [Fact]
    public void Flooding_with_made_up_ids_does_not_grow_the_memory_past_the_limit()
    {
        var submissions = new OneTimeSubmissions(capacity: 100);

        for (var n = 0; n < 10_000; n++)
        {
            Assert.True(submissions.TryClaim(Id(n)));
        }

        // Only the last 100 are still remembered: everything older was dropped, so it can be claimed again.
        Assert.False(submissions.TryClaim(Id(9_999)));
        Assert.False(submissions.TryClaim(Id(9_900)));
        Assert.True(submissions.TryClaim(Id(0)));
        Assert.True(submissions.TryClaim(Id(9_000)));
    }

    [Fact]
    public void It_resolves_from_the_container_with_its_defaults()
    {
        using var provider = new ServiceCollection().AddSingleton<OneTimeSubmissions>().BuildServiceProvider();

        Assert.True(provider.GetRequiredService<OneTimeSubmissions>().TryClaim(OneTimeSubmissions.NewId()));
    }

    // ---- claims that belong to somebody: a user sending a message into a conversation ----

    private const string Ana = "user-ana";
    private const string Luis = "user-luis";
    private const string Chat = "conversation-1";

    [Fact]
    public void A_claim_of_a_user_is_made_once_and_a_replay_is_refused()
    {
        var submissions = new OneTimeSubmissions();

        Assert.True(submissions.TryClaim(Ana, Chat, Id(1)));
        Assert.False(submissions.TryClaim(Ana, Chat, Id(1)));
    }

    [Fact]
    public void The_same_id_under_another_user_or_another_conversation_is_another_claim()
    {
        var submissions = new OneTimeSubmissions();

        Assert.True(submissions.TryClaim(Ana, Chat, Id(1)));
        Assert.True(submissions.TryClaim(Luis, Chat, Id(1)));
        Assert.True(submissions.TryClaim(Ana, "conversation-2", Id(1)));
        Assert.True(submissions.TryClaim(Id(1))); // and the claims without an owner are a world of their own
    }

    [Fact]
    public void Made_up_ids_of_one_user_never_make_the_guard_forget_the_claim_of_another()
    {
        var submissions = new OneTimeSubmissions(capacity: 50);
        Assert.True(submissions.TryClaim(Ana, Chat, Id(1)));

        for (var n = 0; n < 5_000; n++)
        {
            Assert.True(submissions.TryClaim(Luis, Chat, Id(100 + n)));
            submissions.TryClaim(Luis, $"made-up-{n}", Id(100 + n));
            submissions.TryClaim(Id(100 + n));
        }

        Assert.False(submissions.TryClaim(Ana, Chat, Id(1))); // still remembered: the message is not sent twice
    }

    [Fact]
    public void A_user_who_goes_over_the_limit_only_loses_their_own_oldest_claims()
    {
        var submissions = new OneTimeSubmissions();
        for (var n = 0; n <= OneTimeSubmissions.OwnerCapacity; n++)
        {
            Assert.True(submissions.TryClaim(Luis, Chat, Id(n)));
        }

        Assert.True(submissions.TryClaim(Luis, Chat, Id(0))); // the oldest was forgotten
        Assert.False(submissions.TryClaim(Luis, Chat, Id(OneTimeSubmissions.OwnerCapacity))); // the newest was not
    }

    [Fact]
    public void A_claim_of_a_user_is_forgotten_after_fifteen_minutes_and_its_owner_with_it()
    {
        var clock = new ManualClock();
        var submissions = new OneTimeSubmissions(clock);
        Assert.True(submissions.TryClaim(Ana, Chat, Id(1)));
        Assert.Equal(1, submissions.Owners);

        clock.Advance(OneTimeSubmissions.Lifetime + TimeSpan.FromSeconds(1));
        Assert.True(submissions.TryClaim(Luis, Chat, Id(2)));

        Assert.Equal(1, submissions.Owners); // nothing is kept for somebody whose claims all expired
        Assert.True(submissions.TryClaim(Ana, Chat, Id(1)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("forged")]
    public void A_blank_or_foreign_id_is_never_accepted_for_a_user_either(string? id)
        => Assert.False(new OneTimeSubmissions().TryClaim(Ana, Chat, id));

    [Theory]
    [InlineData("", Chat)]
    [InlineData(Ana, "")]
    public void A_claim_without_its_owner_or_its_context_is_refused(string owner, string context)
        => Assert.False(new OneTimeSubmissions().TryClaim(owner, context, Id(1)));
}
