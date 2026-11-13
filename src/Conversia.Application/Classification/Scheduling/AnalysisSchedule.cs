using Conversia.Application.Classification.Llm;
using Conversia.Domain.Enums;

namespace Conversia.Application.Classification.Scheduling;

/// <summary>
/// The deployment's settings of the LLM schedule (<c>Classification:Schedule:*</c>, read whatever the provider so the administrator sees each
/// company's windows).
/// </summary>
/// <param name="TimeZone">The zone of the windows (America/Bogota: no daylight saving).</param>
/// <param name="DefaultNightlyHour">The local hour the night window opens for a company without its own (0 to 23; 1 = 01:00).</param>
/// <param name="NightlyWindowHours">How long the night window stays open (1 to 12): a conversation not reached by then waits for the next night.</param>
/// <param name="SpreadMinutes">Each company's window opens a stable 0..this many minutes late (capped at half the window), so not every company
/// starts at the same minute.</param>
/// <param name="MaxConversationsPerWindow">The most conversations one company has analysed in one window; the rest wait for the next one.</param>
public sealed record AnalysisScheduleSettings(TimeZoneInfo TimeZone, int DefaultNightlyHour, int NightlyWindowHours, int SpreadMinutes, int MaxConversationsPerWindow)
{
    public const string SectionName = "Classification:Schedule";
    public const string TimeZoneKey = SectionName + ":TimeZone";
    public const string DefaultNightlyHourKey = SectionName + ":DefaultNightlyHour";
    public const string NightlyWindowHoursKey = SectionName + ":NightlyWindowHours";
    public const string SpreadMinutesKey = SectionName + ":SpreadMinutes";
    public const string MaxConversationsPerWindowKey = SectionName + ":MaxConversationsPerWindow";

    public static AnalysisScheduleSettings Default { get; } = new(AiQuotaSettings.BogotaTimeZone(), 1, 5, 60, 2000);
}

/// <summary>A company's schedule as the platform administrator set it (<c>Tenant.AiAnalysis*</c>).</summary>
public sealed record TenantAnalysisSchedule(AiAnalysisMode Mode, int? NightlyHour = null, int? IntervalHours = null)
{
    public static TenantAnalysisSchedule Default { get; } = new(AiAnalysisMode.Nightly);
}

/// <summary>
/// One analysis window of a company. <see cref="Start"/> identifies it (the run statistics are kept per window); conversations are claimed
/// from <see cref="OpensAt"/> (the start plus the company's stable spread) until <see cref="End"/>. All UTC.
/// </summary>
public readonly record struct AnalysisWindow(DateTime Start, DateTime End, DateTime OpensAt)
{
    public bool IsOpenAt(DateTime utc) => utc >= OpensAt && utc < End;
}

/// <summary>Pure calculations of the analysis windows (docs/CLASSIFICATION.md, "When the AI reads").</summary>
public static class AnalysisSchedule
{
    /// <summary>The window <paramref name="utc"/> falls in, or the latest one that started before it (which may already be closed).</summary>
    public static AnalysisWindow WindowAt(TenantAnalysisSchedule schedule, Guid tenantId, DateTime utc, AnalysisScheduleSettings settings)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(settings);
        var zone = settings.TimeZone;
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);
        var day = local.Date;

        switch (schedule.Mode)
        {
            case AiAnalysisMode.EveryHours:
            {
                var interval = schedule.IntervalHours ?? 24;
                var slot = local.Hour / interval * interval;
                var start = ToUtc(day.AddHours(slot), zone);
                var end = ToUtc(day.AddHours(Math.Min(slot + interval, 24)), zone);
                return Build(start, end, tenantId, settings.SpreadMinutes);
            }

            case AiAnalysisMode.NearRealTime:
            {
                // Always open: the local day only groups the run statistics.
                var start = ToUtc(day, zone);
                return new AnalysisWindow(start, ToUtc(day.AddDays(1), zone), start);
            }

            default:
            {
                var hour = schedule.NightlyHour ?? settings.DefaultNightlyHour;
                var startLocal = day.AddHours(hour);
                if (startLocal > local)
                {
                    startLocal = startLocal.AddDays(-1);
                }

                var start = ToUtc(startLocal, zone);
                return Build(start, start.AddHours(settings.NightlyWindowHours), tenantId, settings.SpreadMinutes);
            }
        }
    }

    /// <summary>The window that is open at <paramref name="utc"/> (or opening later, inside its spread), or else the next one to open.</summary>
    public static AnalysisWindow NextWindow(TenantAnalysisSchedule schedule, Guid tenantId, DateTime utc, AnalysisScheduleSettings settings)
    {
        var window = WindowAt(schedule, tenantId, utc, settings);
        if (utc < window.End)
        {
            return window;
        }

        if (schedule.Mode == AiAnalysisMode.Nightly)
        {
            // The next start is tomorrow's local hour: one day after the current start, in local time (daylight saving changes the UTC distance).
            var zone = settings.TimeZone;
            var startLocal = TimeZoneInfo.ConvertTimeFromUtc(window.Start, zone).AddDays(1);
            return WindowAt(schedule, tenantId, ToUtc(startLocal, zone), settings);
        }

        return WindowAt(schedule, tenantId, window.End, settings);
    }

    /// <summary>The company's stable delay inside its windows, in minutes (0 to the spread, at most half the window).</summary>
    public static int SpreadOffsetMinutes(Guid tenantId, int spreadMinutes, TimeSpan windowLength)
    {
        var bound = Math.Min(spreadMinutes, (int)(windowLength.TotalMinutes / 2));
        if (bound <= 0)
        {
            return 0;
        }

        // Stable across restarts and instances: derived from the id only (random bits of a version 4 GUID).
        var bytes = tenantId.ToByteArray();
        var value = BitConverter.ToUInt32(bytes, 0) ^ BitConverter.ToUInt32(bytes, 12);
        return (int)(value % (uint)(bound + 1));
    }

    private static AnalysisWindow Build(DateTime start, DateTime end, Guid tenantId, int spreadMinutes)
        => new(start, end, start.AddMinutes(SpreadOffsetMinutes(tenantId, spreadMinutes, end - start)));

    // A local time that does not exist (the hour skipped by daylight saving) opens at the first valid instant after it.
    private static DateTime ToUtc(DateTime local, TimeZoneInfo zone)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(unspecified))
        {
            unspecified = unspecified.AddMinutes(30);
        }

        return TimeZoneInfo.ConvertTimeToUtc(unspecified, zone);
    }
}
