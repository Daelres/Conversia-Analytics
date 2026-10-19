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
}
