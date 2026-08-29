using System.Globalization;
using EVA.Core.Models;

namespace EVA.Infrastructure;

public sealed class BackupScheduler
{
    public bool IsWeeklySnapshotDue(DateTimeOffset currentCheckTime, DateTimeOffset? lastWeeklySnapshotUtc)
    {
        if (lastWeeklySnapshotUtc is null)
        {
            return currentCheckTime.DayOfWeek == DayOfWeek.Sunday || currentCheckTime.Day == 1;
        }

        return !IsSameCalendarWeek(currentCheckTime, lastWeeklySnapshotUtc.Value);
    }

    public bool IsMonthlySnapshotDue(DateTimeOffset currentCheckTime, DateTimeOffset? lastMonthlySnapshotUtc)
    {
        if (lastMonthlySnapshotUtc is null)
        {
            return currentCheckTime.Day == 1;
        }

        return !IsSameCalendarMonth(currentCheckTime, lastMonthlySnapshotUtc.Value);
    }

    public bool IsManualSnapshotDue(bool manualSnapshotRequested)
    {
        return manualSnapshotRequested;
    }

    public bool RequiresNewChainForManualSnapshot(bool manualSnapshotRequested)
    {
        return manualSnapshotRequested;
    }

    public ArchiveType DetermineArchiveType(
        DateTimeOffset currentCheckTime,
        bool hasChanges,
        bool manualSnapshotRequested = false,
        DateTimeOffset? lastSnapshotUtc = null,
        bool weeklySnapshotExistsForCurrentWeek = false,
        bool monthlySnapshotExistsForCurrentMonth = false)
    {
        if (manualSnapshotRequested)
        {
            return ArchiveType.Snapshot;
        }

        if (!hasChanges)
        {
            return ArchiveType.Incremental;
        }

        var weeklyDue = weeklySnapshotExistsForCurrentWeek == false && IsWeeklySnapshotDue(currentCheckTime, lastSnapshotUtc);
        var monthlyDue = monthlySnapshotExistsForCurrentMonth == false && IsMonthlySnapshotDue(currentCheckTime, lastSnapshotUtc);

        return weeklyDue || monthlyDue ? ArchiveType.Snapshot : ArchiveType.Incremental;
    }

    private static bool IsSameCalendarWeek(DateTimeOffset a, DateTimeOffset b)
    {
        var weekA = CultureInfo.InvariantCulture.Calendar.GetWeekOfYear(a.DateTime, CalendarWeekRule.FirstDay, DayOfWeek.Monday);
        var weekB = CultureInfo.InvariantCulture.Calendar.GetWeekOfYear(b.DateTime, CalendarWeekRule.FirstDay, DayOfWeek.Monday);
        return a.Year == b.Year && weekA == weekB;
    }

    private static bool IsSameCalendarMonth(DateTimeOffset a, DateTimeOffset b)
    {
        return a.Year == b.Year && a.Month == b.Month;
    }
}
