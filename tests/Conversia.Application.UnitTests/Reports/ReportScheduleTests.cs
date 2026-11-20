using Conversia.Application.Reports;
using Conversia.Domain.Enums;

namespace Conversia.Application.UnitTests.Reports;

/// <summary>When a report is due and which period it covers (docs/REPORTS.md, "Schedule"): America/Bogota, UTC-5 all year.</summary>
public class ReportScheduleTests
{
    private static readonly TimeZoneInfo Bogota = ReportSchedule.Bogota;

    private static DateTime Utc(int y, int m, int d, int h, int min = 0) => new(y, m, d, h, min, 0, DateTimeKind.Utc);

    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    [Theory]
    // Monday 2026-11-16 07:30 Bogota: last week, due today 07:00.
    [InlineData(2026, 11, 16, 12, 30, 2026, 11, 9, 2026, 11, 16, 2026, 11, 16)]
    // Monday 06:59 Bogota: the hour has not come, so the week before.
    [InlineData(2026, 11, 16, 11, 59, 2026, 11, 2, 2026, 11, 9, 2026, 11, 9)]
    // Sunday night: still last week's report.
    [InlineData(2026, 11, 23, 4, 59, 2026, 11, 9, 2026, 11, 16, 2026, 11, 16)]
    // Across the new year.
    [InlineData(2027, 1, 4, 12, 0, 2026, 12, 28, 2027, 1, 4, 2027, 1, 4)]
    public void Weekly_on_monday_at_seven(int y, int m, int d, int h, int min, int fy, int fm, int fd, int ty, int tm, int td, int dy, int dm, int dd)
    {
        var occurrence = ReportSchedule.Latest(ReportFrequency.Weekly, 1, 7, Utc(y, m, d, h, min), Bogota);

        Assert.Equal(new ReportPeriod(D(fy, fm, fd), D(ty, tm, td)), occurrence.Period);
        Assert.Equal(Utc(dy, dm, dd, 12), occurrence.DueUtc);
    }

    [Fact]
    public void Weekly_on_another_day_still_covers_the_previous_monday_to_sunday_week()
    {
        var wednesday = ReportSchedule.Latest(ReportFrequency.Weekly, 3, 8, Utc(2026, 11, 18, 13), Bogota);
        Assert.Equal(new ReportPeriod(D(2026, 11, 9), D(2026, 11, 16)), wednesday.Period);
        Assert.Equal(Utc(2026, 11, 18, 13), wednesday.DueUtc);

        var tuesday = ReportSchedule.Latest(ReportFrequency.Weekly, 3, 8, Utc(2026, 11, 17, 13), Bogota);
        Assert.Equal(new ReportPeriod(D(2026, 11, 2), D(2026, 11, 9)), tuesday.Period);
        Assert.Equal(Utc(2026, 11, 11, 13), tuesday.DueUtc);
    }

    [Fact]
    public void Daily_follows_the_local_day_not_the_utc_one()
    {
        var midnight = ReportSchedule.Latest(ReportFrequency.Daily, 1, 0, Utc(2026, 11, 18, 5), Bogota);
        Assert.Equal(new ReportPeriod(D(2026, 11, 17), D(2026, 11, 18)), midnight.Period);
        Assert.Equal(Utc(2026, 11, 18, 5), midnight.DueUtc);

        // 23:59 of the 17th in Bogota is already the 18th in UTC.
        var lateNight = ReportSchedule.Latest(ReportFrequency.Daily, 1, 0, Utc(2026, 11, 18, 4, 59), Bogota);
        Assert.Equal(new ReportPeriod(D(2026, 11, 16), D(2026, 11, 17)), lateNight.Period);
    }

    [Theory]
    [InlineData(2026, 12, 1, 12, 2026, 11, 1, 2026, 12, 1)]
    [InlineData(2026, 11, 30, 23, 2026, 10, 1, 2026, 11, 1)]
    [InlineData(2027, 1, 1, 12, 2026, 12, 1, 2027, 1, 1)]
    [InlineData(2028, 3, 1, 12, 2028, 2, 1, 2028, 3, 1)]
    public void Monthly_on_the_first_day(int y, int m, int d, int h, int fy, int fm, int fd, int ty, int tm, int td)
    {
        var occurrence = ReportSchedule.Latest(ReportFrequency.Monthly, 1, 7, Utc(y, m, d, h), Bogota);

        Assert.Equal(new ReportPeriod(D(fy, fm, fd), D(ty, tm, td)), occurrence.Period);
    }

    [Fact]
    public void A_report_is_due_once_its_hour_passed_within_the_catch_up_window_and_never_for_a_time_before_the_definition_existed()
    {
        var occurrence = new ReportOccurrence(new ReportPeriod(D(2026, 11, 9), D(2026, 11, 16)), Utc(2026, 11, 16, 12));
        var created = Utc(2026, 11, 1, 0);
        var catchUp = TimeSpan.FromHours(24);

        Assert.True(ReportSchedule.IsDue(occurrence, Utc(2026, 11, 16, 12), created, catchUp));
        Assert.True(ReportSchedule.IsDue(occurrence, Utc(2026, 11, 17, 12), created, catchUp));
        Assert.False(ReportSchedule.IsDue(occurrence, Utc(2026, 11, 16, 11, 59), created, catchUp));
        Assert.False(ReportSchedule.IsDue(occurrence, Utc(2026, 11, 17, 12, 1), created, catchUp));
        Assert.False(ReportSchedule.IsDue(occurrence, Utc(2026, 11, 16, 13), Utc(2026, 11, 16, 12, 30), catchUp));
    }

    [Fact]
    public void The_preview_covers_the_last_complete_period_whatever_the_hour()
    {
        var monday = Utc(2026, 11, 16, 11); // 06:00 in Bogota, before the usual send hour
        Assert.Equal(new ReportPeriod(D(2026, 11, 9), D(2026, 11, 16)), ReportSchedule.LastCompleted(ReportFrequency.Weekly, monday, Bogota));
        Assert.Equal(new ReportPeriod(D(2026, 11, 15), D(2026, 11, 16)), ReportSchedule.LastCompleted(ReportFrequency.Daily, monday, Bogota));
        Assert.Equal(new ReportPeriod(D(2026, 10, 1), D(2026, 11, 1)), ReportSchedule.LastCompleted(ReportFrequency.Monthly, monday, Bogota));
    }

    [Fact]
    public void Property_the_latest_occurrence_has_passed_the_next_one_has_not_and_the_period_ends_on_or_before_the_send_day()
    {
        var random = new Random(20261118);
        foreach (var frequency in Enum.GetValues<ReportFrequency>())
        {
            for (var i = 0; i < 2000; i++)
            {
                var now = Utc(2026, 1, 1, 0).AddMinutes(random.Next(0, 3 * 365 * 24 * 60));
                var (day, hour) = (random.Next(1, 8), random.Next(0, 24));

                var latest = ReportSchedule.Latest(frequency, day, hour, now, Bogota);
                var next = ReportSchedule.Latest(frequency, day, hour, latest.DueUtc.AddDays(frequency == ReportFrequency.Monthly ? 32 : 8), Bogota);

                Assert.True(latest.DueUtc <= now, $"{frequency} {now:o}");
                var following = ReportSchedule.Next(frequency, day, hour, now, Bogota);
                Assert.True(following.DueUtc > now);
                Assert.Equal(latest.Period.To, following.Period.From);
                Assert.Equal(following, ReportSchedule.Latest(frequency, day, hour, following.DueUtc, Bogota));
                Assert.True(next.DueUtc > now, $"{frequency} {now:o}: the next one {next.DueUtc:o}");
                Assert.True(latest.Period.To <= DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(latest.DueUtc, Bogota)));
                Assert.Equal(TimeZoneInfo.ConvertTimeFromUtc(latest.DueUtc, Bogota).Hour, hour);
                Assert.Equal(frequency switch { ReportFrequency.Daily => 1, ReportFrequency.Weekly => 7, _ => latest.Period.Days }, latest.Period.Days);
            }
        }
    }
}
