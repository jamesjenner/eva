using System.Globalization;

namespace EVA.Core;

public static class ArchiveDateGrouping
{
    private const string FilenameTimestampFormat = "yyyy_MM_dd_HHmmss";
    private const int FilenameTimestampLength = 17;

    public static IReadOnlyList<ArchiveDateGroup> GroupArchives(
        IEnumerable<string> archivePaths,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(archivePaths);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var localNow = timeProvider.GetLocalNow();
        var today = DateOnly.FromDateTime(localNow.DateTime);
        var yesterday = today.AddDays(-1);
        var currentWeekStart = GetMonday(today);
        var previousWeekStart = currentWeekStart.AddDays(-7);
        var currentMonthStart = new DateOnly(today.Year, today.Month, 1);
        var previousMonthStart = currentMonthStart.AddMonths(-1);

        var todayArchives = new List<ParsedArchive>();
        var yesterdayArchives = new List<ParsedArchive>();
        var weekdays = new Dictionary<DateOnly, List<ParsedArchive>>();
        var previousWeekArchives = new List<ParsedArchive>();
        var currentMonthArchives = new List<ParsedArchive>();
        var previousMonthArchives = new List<ParsedArchive>();
        var olderMonths = new Dictionary<DateOnly, List<ParsedArchive>>();

        foreach (var archivePath in archivePaths)
        {
            if (!TryParseLocalTimestamp(archivePath, timeProvider.LocalTimeZone, out var localTimestamp))
            {
                continue;
            }

            var localDate = DateOnly.FromDateTime(localTimestamp.DateTime);
            var archive = new ParsedArchive(archivePath, localTimestamp);

            if (localDate == today)
            {
                todayArchives.Add(archive);
            }
            else if (localDate == yesterday)
            {
                yesterdayArchives.Add(archive);
            }
            else if (localDate < today && localDate >= currentWeekStart)
            {
                Add(weekdays, localDate, archive);
            }
            else if (localDate >= previousWeekStart && localDate < currentWeekStart)
            {
                previousWeekArchives.Add(archive);
            }
            else if (localDate >= currentMonthStart && localDate < today)
            {
                currentMonthArchives.Add(archive);
            }
            else if (localDate >= previousMonthStart && localDate < currentMonthStart)
            {
                previousMonthArchives.Add(archive);
            }
            else if (localDate < previousMonthStart)
            {
                var month = new DateOnly(localDate.Year, localDate.Month, 1);
                Add(olderMonths, month, archive);
            }
        }

        var culture = CultureInfo.CurrentCulture;
        var groups = new List<ArchiveDateGroup>();
        AddGroup(groups, "Today", todayArchives);
        AddGroup(groups, "Yesterday", yesterdayArchives);

        foreach (var (date, archives) in weekdays.OrderByDescending(pair => pair.Key))
        {
            AddGroup(groups, date.ToDateTime(TimeOnly.MinValue).ToString("dddd", culture), archives);
        }

        AddGroup(groups, "Last Week", previousWeekArchives);
        AddGroup(groups, "This Month", currentMonthArchives);
        AddGroup(groups, "Last Month", previousMonthArchives);

        foreach (var (month, archives) in olderMonths.OrderByDescending(pair => pair.Key))
        {
            AddGroup(groups, month.ToDateTime(TimeOnly.MinValue).ToString("MMMM yyyy", culture), archives);
        }

        return groups;
    }

    private static bool TryParseLocalTimestamp(
        string archivePath,
        TimeZoneInfo localTimeZone,
        out DateTimeOffset localTimestamp)
    {
        localTimestamp = default;
        var fileName = Path.GetFileNameWithoutExtension(archivePath);
        if (fileName.Length < FilenameTimestampLength ||
            !DateTimeOffset.TryParseExact(
                fileName.AsSpan(0, FilenameTimestampLength),
                FilenameTimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var utcTimestamp))
        {
            return false;
        }

        localTimestamp = TimeZoneInfo.ConvertTime(utcTimestamp, localTimeZone);
        return true;
    }

    private static DateOnly GetMonday(DateOnly date)
    {
        var daysSinceMonday = ((int)date.DayOfWeek + 6) % 7;
        return date.AddDays(-daysSinceMonday);
    }

    private static void Add<TKey>(IDictionary<TKey, List<ParsedArchive>> groups, TKey key, ParsedArchive archive)
        where TKey : notnull
    {
        if (!groups.TryGetValue(key, out var archives))
        {
            archives = [];
            groups.Add(key, archives);
        }

        archives.Add(archive);
    }

    private static void AddGroup(List<ArchiveDateGroup> groups, string heading, List<ParsedArchive> archives)
    {
        if (archives.Count == 0)
        {
            return;
        }

        groups.Add(new ArchiveDateGroup(
            heading,
            archives
                .OrderByDescending(archive => archive.LocalTimestamp)
                .ThenBy(archive => archive.Path, StringComparer.OrdinalIgnoreCase)
                .Select(archive => archive.Path)
                .ToArray()));
    }

    private sealed record ParsedArchive(string Path, DateTimeOffset LocalTimestamp);
}

public sealed record ArchiveDateGroup(string Heading, IReadOnlyList<string> ArchivePaths);