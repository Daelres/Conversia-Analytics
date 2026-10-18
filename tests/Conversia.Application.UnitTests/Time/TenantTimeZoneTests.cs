using Conversia.Application.Time;

namespace Conversia.Application.UnitTests.Time;

public class TenantTimeZoneTests
{
    private static readonly TenantTimeZone Bogota = new("America/Bogota");

    [Fact]
    public void ADayInBogota_BeginsAtFiveInTheMorningUtc()
        => Assert.Equal(new DateTime(2026, 10, 18, 5, 0, 0, DateTimeKind.Utc), Bogota.StartOfDayUtc(new DateOnly(2026, 10, 18)));

    [Fact]
    public void TheLocalDate_ChangesAtLocalMidnight_NotAtUtcMidnight()
    {
        Assert.Equal(new DateOnly(2026, 10, 18), Bogota.LocalDate(new DateTime(2026, 10, 19, 4, 59, 59, DateTimeKind.Utc))); // 23:59:59 in Bogota
        Assert.Equal(new DateOnly(2026, 10, 19), Bogota.LocalDate(new DateTime(2026, 10, 19, 5, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(new DateOnly(2026, 10, 17), Bogota.LocalDate(new DateTime(2026, 10, 18, 4, 59, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void Bogota_HasNoDaylightSaving_SoEveryDayIsTwentyFourHours()
    {
        var start = new DateOnly(2026, 1, 1);
        for (var i = 0; i < 365; i += 30)
        {
            var day = start.AddDays(i);
            Assert.Equal(TimeSpan.FromHours(24), Bogota.StartOfDayUtc(day.AddDays(1)) - Bogota.StartOfDayUtc(day));
        }
    }

    [Fact]
    public void AZoneWithDaylightSaving_HasADayOfTwentyThreeHours_AndOneOfTwentyFive()
    {
        var newYork = new TenantTimeZone("America/New_York");

        // 2026-03-08: clocks go forward at 02:00. 2026-11-01: they go back at 02:00.
        Assert.Equal(TimeSpan.FromHours(23), newYork.StartOfDayUtc(new DateOnly(2026, 3, 9)) - newYork.StartOfDayUtc(new DateOnly(2026, 3, 8)));
        Assert.Equal(TimeSpan.FromHours(25), newYork.StartOfDayUtc(new DateOnly(2026, 11, 2)) - newYork.StartOfDayUtc(new DateOnly(2026, 11, 1)));
    }

    [Fact]
    public void WhenMidnightDoesNotExist_TheDayBeginsAtTheFirstValidMoment()
    {
        // Chile moves its clocks forward at midnight, so 2026-09-06 00:00 never happens there.
        var santiago = new TenantTimeZone("America/Santiago");

        var start = santiago.StartOfDayUtc(new DateOnly(2026, 9, 6));

        var local = TimeZoneInfo.ConvertTimeFromUtc(start, santiago.Zone);
        Assert.Equal(new DateOnly(2026, 9, 6), DateOnly.FromDateTime(local));
        Assert.Equal(new TimeOnly(1, 0), TimeOnly.FromDateTime(local));
    }

    [Fact]
    public void AnUnknownZone_Throws()
        => Assert.Throws<TimeZoneNotFoundException>(() => new TenantTimeZone("Mars/Olympus_Mons"));
}
