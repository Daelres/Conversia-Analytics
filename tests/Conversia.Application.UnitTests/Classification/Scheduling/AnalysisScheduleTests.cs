using Conversia.Application.Classification.Scheduling;
using Conversia.Domain.Enums;

namespace Conversia.Application.UnitTests.Classification.Scheduling;

/// <summary>The analysis windows: local time (America/Bogota, no daylight saving), boundaries, intervals and the per-company spread.</summary>
public class AnalysisScheduleTests
{
    private static readonly Guid Tenant = Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7");

    // No spread: windows open exactly at their start, so the boundaries can be asserted to the second.
    private static readonly AnalysisScheduleSettings NoSpread = AnalysisScheduleSettings.Default with { SpreadMinutes = 0 };

    private static DateTime Utc(int day, int hour, int minute = 0) => new(2026, 11, day, hour, minute, 0, DateTimeKind.Utc);

    private static readonly TenantAnalysisSchedule Nightly = TenantAnalysisSchedule.Default;

    [Fact]
    public void TheDefaultNightlyWindow_OpensAt0100Bogota_WhichIs0600Utc_AndLastsFiveHours()
    {
        var window = AnalysisSchedule.WindowAt(Nightly, Tenant, Utc(12, 7), NoSpread);

        Assert.Equal((Utc(12, 6), Utc(12, 11), Utc(12, 6)), (window.Start, window.End, window.OpensAt));
        Assert.True(window.IsOpenAt(Utc(12, 7)));
    }

    [Theory]
    [InlineData(5, 59, false)] // 00:59 Bogota: before the window
    [InlineData(6, 0, true)]   // 01:00: the first second of the window
    [InlineData(10, 59, true)]
    [InlineData(11, 0, false)] // 06:00: closed
    public void TheNightlyWindowBoundaries(int hour, int minute, bool open)
    {
        var now = Utc(12, hour, minute);

        Assert.Equal(open, AnalysisSchedule.WindowAt(Nightly, Tenant, now, NoSpread).IsOpenAt(now));
    }

    [Fact]
    public void BeforeTonightsWindow_TheCurrentWindowIsLastNights_AndTheNextIsTonights()
    {
        var now = Utc(12, 5, 30); // 00:30 Bogota on the 12th

        Assert.Equal(Utc(11, 6), AnalysisSchedule.WindowAt(Nightly, Tenant, now, NoSpread).Start);
        Assert.Equal(Utc(12, 6), AnalysisSchedule.NextWindow(Nightly, Tenant, now, NoSpread).Start);
    }

    [Fact]
    public void AfterTheWindowCloses_TheNextIsTomorrowNight_AndAnOpenWindowIsItsOwnNext()
    {
        Assert.Equal(Utc(13, 6), AnalysisSchedule.NextWindow(Nightly, Tenant, Utc(12, 15), NoSpread).Start);
        Assert.Equal(Utc(12, 6), AnalysisSchedule.NextWindow(Nightly, Tenant, Utc(12, 8), NoSpread).Start);
    }

    [Fact]
    public void ACompanyHour_ReplacesTheDefault_AndAWindowCanCrossLocalMidnight()
    {
        var at22 = new TenantAnalysisSchedule(AiAnalysisMode.Nightly, NightlyHour: 22);

        // 22:00 Bogota on the 11th = 03:00 UTC on the 12th; open until 03:00 Bogota (08:00 UTC).
        var window = AnalysisSchedule.WindowAt(at22, Tenant, Utc(12, 6), NoSpread);

        Assert.Equal((Utc(12, 3), Utc(12, 8)), (window.Start, window.End));
    }

    [Fact]
    public void TheDeploymentDefaultHourAndLength_AreUsed()
    {
        var settings = NoSpread with { DefaultNightlyHour = 3, NightlyWindowHours = 2 };

        var window = AnalysisSchedule.WindowAt(Nightly, Tenant, Utc(12, 9), settings);

        Assert.Equal((Utc(12, 8), Utc(12, 10)), (window.Start, window.End));
    }

    [Theory]
    [InlineData(6, 7, 5, 11)]   // every 6 h: 00, 06, 12, 18 Bogota; 02:00 Bogota (07 UTC) is in 00-06 (05-11 UTC)
    [InlineData(6, 18, 17, 23)] // 13:00 Bogota is in 12-18 (17-23 UTC)
    [InlineData(1, 7, 7, 8)]    // every hour
    [InlineData(24, 7, 5, 29)]   // once a day, from local midnight to local midnight (29 = next day 05 UTC)
    public void EveryHours_CountsWindowsFromLocalMidnight(int interval, int nowHour, int expectedStartHour, int expectedEndHour)
    {
        var schedule = new TenantAnalysisSchedule(AiAnalysisMode.EveryHours, IntervalHours: interval);

        var window = AnalysisSchedule.WindowAt(schedule, Tenant, Utc(12, nowHour, 30), NoSpread);

        Assert.Equal(Utc(12, 0).AddHours(expectedStartHour), window.Start);
        Assert.Equal(Utc(12, 0).AddHours(expectedEndHour), window.End);
    }

    [Fact]
    public void EveryFiveHours_TheLastWindowOfTheDayIsCutAtLocalMidnight()
    {
        var schedule = new TenantAnalysisSchedule(AiAnalysisMode.EveryHours, IntervalHours: 5);

        // 22:00 Bogota on the 12th (03:00 UTC on the 13th) is in 20:00-24:00 local.
        var window = AnalysisSchedule.WindowAt(schedule, Tenant, Utc(13, 3), NoSpread);

        Assert.Equal((Utc(13, 1), Utc(13, 5)), (window.Start, window.End));
        Assert.Equal(Utc(13, 5), AnalysisSchedule.NextWindow(schedule, Tenant, Utc(13, 5), NoSpread).Start);
    }

    [Fact]
    public void NearRealTime_IsAlwaysOpen_WithTheLocalDayAsItsStatisticsWindow()
    {
        var schedule = new TenantAnalysisSchedule(AiAnalysisMode.NearRealTime);

        var window = AnalysisSchedule.WindowAt(schedule, Tenant, Utc(12, 4), AnalysisScheduleSettings.Default);

        Assert.Equal((Utc(11, 5), Utc(12, 5), Utc(11, 5)), (window.Start, window.End, window.OpensAt));
        Assert.True(window.IsOpenAt(Utc(12, 4)));
    }

    [Fact]
    public void TheSpread_IsStablePerCompany_WithinItsBound_AndDiffersBetweenCompanies()
    {
        var offsets = Enumerable.Range(0, 200).Select(_ => Guid.NewGuid())
            .Select(id => (id, AnalysisSchedule.SpreadOffsetMinutes(id, 60, TimeSpan.FromHours(5)))).ToList();

        Assert.All(offsets, o => Assert.InRange(o.Item2, 0, 60));
        Assert.All(offsets, o => Assert.Equal(o.Item2, AnalysisSchedule.SpreadOffsetMinutes(o.id, 60, TimeSpan.FromHours(5))));
        Assert.True(offsets.Select(o => o.Item2).Distinct().Count() > 20, "the companies should be spread over the hour");
        // Never more than half a short window: a one-hour window keeps at least 30 minutes of work.
        Assert.All(offsets, o => Assert.InRange(AnalysisSchedule.SpreadOffsetMinutes(o.id, 60, TimeSpan.FromHours(1)), 0, 30));
        Assert.Equal(0, AnalysisSchedule.SpreadOffsetMinutes(Tenant, 0, TimeSpan.FromHours(5)));
    }

    [Fact]
    public void TheSpread_DelaysTheOpening_NotTheWindowsIdentity()
    {
        var spread = AnalysisSchedule.SpreadOffsetMinutes(Tenant, 60, TimeSpan.FromHours(5));
        var window = AnalysisSchedule.WindowAt(Nightly, Tenant, Utc(12, 9), AnalysisScheduleSettings.Default);

        Assert.Equal((Utc(12, 6), Utc(12, 6).AddMinutes(spread)), (window.Start, window.OpensAt));
    }

    [Fact]
    public void InAZoneWithDaylightSaving_ANonExistentLocalHourOpensAtTheFirstValidInstant()
    {
        if (!TimeZoneInfo.TryFindSystemTimeZoneById("America/New_York", out var newYork))
        {
            return; // no zone data in this image: Bogota (the default) has no daylight saving anyway
        }

        // 8 March 2026: 02:00 local does not exist in New York (clocks jump to 03:00 = 07:00 UTC).
        var settings = NoSpread with { TimeZone = newYork };
        var schedule = new TenantAnalysisSchedule(AiAnalysisMode.Nightly, NightlyHour: 2);

        var window = AnalysisSchedule.WindowAt(schedule, Tenant, new DateTime(2026, 3, 8, 9, 0, 0, DateTimeKind.Utc), settings);

        Assert.Equal(new DateTime(2026, 3, 8, 7, 0, 0, DateTimeKind.Utc), window.Start);
    }
}
