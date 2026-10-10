using System.Text;
using System.Globalization;
using EVA.App;
using EVA.Core;
using EVA.Core.Models;
using EVA.Infrastructure;
using Xunit;

namespace EVA.Tests;

public sealed class ArchiveContentProjectionTests : IDisposable
{
    private readonly string _tempDirectory = CreateTempDirectory();
    private readonly FakePasswordStore _passwordStore = new();

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task BuildTreeUsesAuthenticatedArchiveEntriesAndNormalizesPaths()
    {
        var sourceDirectory = Path.Combine(_tempDirectory, "source");
        Directory.CreateDirectory(Path.Combine(sourceDirectory, "docs"));
        await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "docs", "readme.txt"), "archive text");
        var archivePath = Path.Combine(_tempDirectory, "2026_10_10_120000_full_01.eva");
        var fileEntries = new[]
        {
            new FileEntry
            {
                RelativePath = "docs\\readme.txt",
                Operation = FileOperation.Added,
                FileSize = 12,
                LastModifiedUtc = DateTimeOffset.UtcNow,
                Sha256 = "hash"
            },
            new FileEntry
            {
                RelativePath = "docs\\removed.txt",
                Operation = FileOperation.Deleted,
                FileSize = 0,
                LastModifiedUtc = DateTimeOffset.UtcNow
            }
        };
        var manifest = new ArchiveManifest
        {
            FormatVersion = 1,
            ArchiveId = "archive-1",
            ChainId = "chain-1",
            SourceId = "source-1",
            CreatedUtc = DateTimeOffset.UtcNow,
            ArchiveType = ArchiveType.Full,
            Deleted =
            [
                new DeletedEntry
                {
                    RelativePath = "old\\entry.txt",
                    DeletedAtUtc = DateTimeOffset.UtcNow
                }
            ]
        };

        await new ArchiveWriter(sourceDirectory).WriteArchiveAsync(
            manifest,
            fileEntries,
            [new DirectoryEntry { RelativePath = "docs\\", Operation = FileOperation.Added }],
            archivePath,
            _passwordStore.GetPassword()!);

        var reader = new ArchiveReader();
        var archiveManifest = await reader.ReadManifestAsync(archivePath, _passwordStore.GetPassword()!);
        var tree = ArchiveContentProjection.BuildTree(
            archiveManifest.Files,
            archiveManifest.Directories,
            archiveManifest.Deleted);

        var docs = Assert.Single(tree, node => node.Name == "docs");
        Assert.True(docs.IsDirectory);
        Assert.Contains(docs.Children, node => node.Name == "readme.txt");
        Assert.Contains(docs.Children, node => node.Name == "removed.txt" && node.IsDeleted);
        Assert.Contains(tree, node => node.Name == "old" && node.IsDirectory);

        var content = await reader.ReadFileContentAsync(
            archivePath,
            "docs\\readme.txt",
            _passwordStore.GetPassword()!);
        Assert.Equal("archive text", Encoding.UTF8.GetString(content));
    }

    [Fact]
    public void ArchiveTypeLabelUsesFilenameConventionAndManualMarker()
    {
        Assert.Equal("Full", ArchiveContentProjection.GetArchiveTypeLabel(
            "2026_10_10_120000_full_01.eva", ArchiveType.Incremental));
        Assert.Equal("Incremental", ArchiveContentProjection.GetArchiveTypeLabel(
            "2026_10_10_120000_incremental_01.eva", ArchiveType.Full));
        Assert.Equal("Full (Manual)", ArchiveContentProjection.GetArchiveTypeLabel(
            "2026_10_10_120000_full_01.eva", ArchiveType.Full, isManual: true));
    }

    [Fact]
    public async Task TextPreviewSupportsShortAsciiFileWithoutBom()
    {
        const string text = "plain ASCII preview";
        var path = Path.Combine(_tempDirectory, "plain-ascii.txt");
        await File.WriteAllBytesAsync(path, Encoding.ASCII.GetBytes(text));

        var preview = ArchiveContentProjection.CreateTextPreview(await File.ReadAllBytesAsync(path));

        Assert.True(preview.IsSupported);
        Assert.Equal(text, preview.Text);
        Assert.False(preview.IsTruncated);
    }

    [Fact]
    public async Task TextPreviewSupportsUtf8FileWithoutBom()
    {
        const string text = "café 東京";
        var content = Encoding.UTF8.GetBytes(text);
        Assert.False(content.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }));
        var path = Path.Combine(_tempDirectory, "utf8-no-bom.txt");
        await File.WriteAllBytesAsync(path, content);

        var preview = ArchiveContentProjection.CreateTextPreview(await File.ReadAllBytesAsync(path));

        Assert.True(preview.IsSupported);
        Assert.Equal(text, preview.Text);
        Assert.False(preview.IsTruncated);
    }

    [Fact]
    public void ArchiveRowViewModelExposesDisplayTextAndTypeLabel()
    {
        var headingRow = ArchiveListRowViewModel.ForHeader("Today");
        var fullRow = ArchiveListRowViewModel.ForArchive(
            "2026_10_10_120000_full_01.eva",
            new ArchiveHeader
            {
                ArchiveType = ArchiveType.Full,
                CreatedUtc = new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero)
            });
        var incrementalRow = ArchiveListRowViewModel.ForArchive(
            "2026_10_10_120000_incremental_01.eva",
            new ArchiveHeader
            {
                ArchiveType = ArchiveType.Incremental,
                CreatedUtc = new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero)
            });

        Assert.True(headingRow.IsHeader);
        Assert.False(headingRow.IsSelectable);
        Assert.Equal("Today", headingRow.Heading);
        Assert.False(fullRow.IsHeader);
        Assert.True(fullRow.IsSelectable);
        Assert.NotEmpty(fullRow.TimestampLabel);
        Assert.Equal("Full", fullRow.TypeLabel);
        Assert.NotEmpty(incrementalRow.TimestampLabel);
        Assert.Equal("Incremental", incrementalRow.TypeLabel);
    }

    [Fact]
    public void ArchiveGroupsPutFutureArchivesOneMinuteAndOneYearAheadInOther()
    {
        var timeProvider = CreateTimeProvider(new DateTime(2026, 5, 14, 12, 0, 0), TimeSpan.Zero);
        var oneMinuteAhead = CreateArchiveAtLocal(new DateTime(2026, 5, 14, 12, 1, 0), timeProvider.LocalTimeZone);
        var oneYearAhead = CreateArchiveAtLocal(new DateTime(2027, 5, 14, 12, 0, 0), timeProvider.LocalTimeZone);

        var groups = GroupArchives(timeProvider);

        Assert.Equal("Other", groups[^1].Heading);
        Assert.Equal(new[] { oneYearAhead, oneMinuteAhead }, groups[^1].ArchivePaths);
    }

    [Fact]
    public void ArchiveGroupsPutUnparseableAndInvalidDateFilenamesInOther()
    {
        var timeProvider = CreateTimeProvider(new DateTime(2026, 5, 14, 12, 0, 0), TimeSpan.Zero);
        var backup = CreateArchiveFile("backup.eva");
        var invalidDate = CreateArchiveFile("2026_13_45_990000_full_01.eva");

        var groups = GroupArchives(timeProvider);

        Assert.Equal("Other", Assert.Single(groups).Heading);
        Assert.Equal(new[] { backup, invalidDate }.OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase),
            groups[0].ArchivePaths.OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void OtherGroupIsLastAndOmittedWhenEmpty()
    {
        var timeProvider = CreateTimeProvider(new DateTime(2026, 5, 14, 12, 0, 0), TimeSpan.Zero);
        CreateArchiveAtLocal(new DateTime(2026, 5, 14, 11, 0, 0), timeProvider.LocalTimeZone);

        Assert.DoesNotContain(GroupArchives(timeProvider), group => group.Heading == "Other");

        CreateArchiveFile("backup.eva");
        Assert.Equal("Other", GroupArchives(timeProvider)[^1].Heading);
    }

    [Fact]
    public void OtherRowsDisplayFilenameAndUseFilenameTypeOrUnknown()
    {
        var timeProvider = CreateTimeProvider(new DateTime(2026, 5, 14, 12, 0, 0), TimeSpan.Zero);
        var incrementalPath = CreateArchiveFile("backup_incremental_copy.eva");
        var unknownPath = CreateArchiveFile("backup.eva");
        var header = new ArchiveHeader
        {
            ArchiveType = ArchiveType.Full,
            CreatedUtc = DateTimeOffset.UnixEpoch
        };

        var incremental = ArchiveListRowViewModel.ForOtherArchive(incrementalPath, header);
        var unknown = ArchiveListRowViewModel.ForOtherArchive(unknownPath, header);

        Assert.True(incremental.IsSelectable);
        Assert.Equal("backup_incremental_copy.eva", incremental.DisplayText);
        Assert.Equal("Incremental", incremental.TypeLabel);
        Assert.Equal("backup.eva", unknown.DisplayText);
        Assert.Equal("Unknown", unknown.TypeLabel);
    }

    [Fact]
    public void MixedEvaDirectoryProducesOneSelectableArchiveRowPerFile()
    {
        var timeProvider = CreateTimeProvider(new DateTime(2026, 5, 14, 12, 0, 0), TimeSpan.Zero);
        CreateArchiveAtLocal(new DateTime(2026, 5, 14, 11, 0, 0), timeProvider.LocalTimeZone);
        CreateArchiveAtLocal(new DateTime(2026, 5, 14, 12, 1, 0), timeProvider.LocalTimeZone);
        CreateArchiveFile("backup_incremental.eva");
        CreateArchiveFile("backup.eva");

        var archiveFiles = Directory.EnumerateFiles(_tempDirectory, "*.eva").ToArray();
        var archiveRows = archiveFiles.ToDictionary(
            path => path,
            path => ArchiveListRowViewModel.ForArchive(
                path,
                new ArchiveHeader
                {
                    ArchiveType = ArchiveType.Full,
                    CreatedUtc = DateTimeOffset.UnixEpoch
                }),
            StringComparer.OrdinalIgnoreCase);
        var groups = GroupArchives(timeProvider);
        var rows = ArchiveListRowViewModel.BuildRows(groups, archiveRows);

        Assert.Equal(archiveFiles.Length, rows.Count(row => row.IsArchive));
        Assert.All(rows.Where(row => row.IsArchive), row => Assert.True(row.IsSelectable));
    }

    [Fact]
    public void ArchiveGroupsFollowLocalMidnightAcrossUtcDateBoundary()
    {
        var timeProvider = CreateTimeProvider(new DateTime(2026, 5, 12, 0, 10, 0), TimeSpan.FromHours(2));
        var yesterday = CreateArchiveAtLocal(new DateTime(2026, 5, 11, 23, 59, 0), timeProvider.LocalTimeZone);
        var today = CreateArchiveAtLocal(new DateTime(2026, 5, 12, 0, 1, 0), timeProvider.LocalTimeZone);

        var groups = GroupArchives(timeProvider);

        Assert.Equal(new[] { "Today", "Yesterday" }, groups.Select(group => group.Heading));
        Assert.Equal(today, Assert.Single(groups[0].ArchivePaths));
        Assert.Equal(yesterday, Assert.Single(groups[1].ArchivePaths));
    }

    [Fact]
    public void ArchiveGroupsSeparateCurrentWeekDaysFromLastWeeksSunday()
    {
        var timeProvider = CreateTimeProvider(new DateTime(2026, 5, 14, 12, 0, 0), TimeSpan.Zero);
        CreateArchiveAtLocal(new DateTime(2026, 5, 13, 12, 0, 0), timeProvider.LocalTimeZone);
        CreateArchiveAtLocal(new DateTime(2026, 5, 12, 12, 0, 0), timeProvider.LocalTimeZone);
        CreateArchiveAtLocal(new DateTime(2026, 5, 11, 12, 0, 0), timeProvider.LocalTimeZone);
        CreateArchiveAtLocal(new DateTime(2026, 5, 10, 12, 0, 0), timeProvider.LocalTimeZone);

        var groups = GroupArchives(timeProvider);

        Assert.Equal(
            new[]
            {
                "Yesterday",
                new DateTime(2026, 5, 12).ToString("dddd", CultureInfo.CurrentCulture),
                new DateTime(2026, 5, 11).ToString("dddd", CultureInfo.CurrentCulture),
                "Last Week"
            },
            groups.Select(group => group.Heading));
    }

    [Fact]
    public void SundayYesterdayIsNotGroupedAsLastWeek()
    {
        var timeProvider = CreateTimeProvider(new DateTime(2026, 5, 11, 12, 0, 0), TimeSpan.Zero);
        var sunday = CreateArchiveAtLocal(new DateTime(2026, 5, 10, 12, 0, 0), timeProvider.LocalTimeZone);
        CreateArchiveAtLocal(new DateTime(2026, 5, 4, 12, 0, 0), timeProvider.LocalTimeZone);

        var groups = GroupArchives(timeProvider);

        Assert.Equal(new[] { "Yesterday", "Last Week" }, groups.Select(group => group.Heading));
        Assert.Equal(sunday, Assert.Single(groups[0].ArchivePaths));
    }

    [Fact]
    public void ArchiveGroupsHandleMonthBoundaryAndJanuaryPreviousYear()
    {
        var februaryProvider = CreateTimeProvider(new DateTime(2026, 2, 10, 12, 0, 0), TimeSpan.Zero);
        CreateArchiveAtLocal(new DateTime(2026, 1, 31, 23, 59, 0), februaryProvider.LocalTimeZone);
        Assert.Equal(new[] { "Last Month" }, GroupArchives(februaryProvider).Select(group => group.Heading));

        foreach (var file in Directory.EnumerateFiles(_tempDirectory))
        {
            File.Delete(file);
        }

        var januaryProvider = CreateTimeProvider(new DateTime(2026, 1, 15, 12, 0, 0), TimeSpan.Zero);
        CreateArchiveAtLocal(new DateTime(2025, 12, 31, 23, 59, 0), januaryProvider.LocalTimeZone);
        CreateArchiveAtLocal(new DateTime(2025, 11, 30, 23, 59, 0), januaryProvider.LocalTimeZone);

        Assert.Equal(
            new[] { "Last Month", new DateTime(2025, 11, 1).ToString("MMMM yyyy", CultureInfo.CurrentCulture) },
            GroupArchives(januaryProvider).Select(group => group.Heading));
    }

    [Fact]
    public void ArchiveGroupingOmitsEmptyGroups()
    {
        var timeProvider = CreateTimeProvider(new DateTime(2026, 5, 14, 12, 0, 0), TimeSpan.Zero);
        CreateArchiveAtLocal(new DateTime(2026, 5, 14, 11, 0, 0), timeProvider.LocalTimeZone);
        CreateArchiveAtLocal(new DateTime(2026, 1, 14, 11, 0, 0), timeProvider.LocalTimeZone);

        var groups = GroupArchives(timeProvider);

        Assert.Equal(
            new[] { "Today", new DateTime(2026, 1, 1).ToString("MMMM yyyy", CultureInfo.CurrentCulture) },
            groups.Select(group => group.Heading));
    }

    [Fact]
    public void ArchiveGroupsAndArchivesAreOrderedNewestFirst()
    {
        var timeProvider = CreateTimeProvider(new DateTime(2026, 5, 14, 12, 0, 0), TimeSpan.Zero);
        var todayEarlier = CreateArchiveAtLocal(new DateTime(2026, 5, 14, 9, 0, 0), timeProvider.LocalTimeZone);
        var todayLater = CreateArchiveAtLocal(new DateTime(2026, 5, 14, 11, 0, 0), timeProvider.LocalTimeZone);
        CreateArchiveAtLocal(new DateTime(2026, 5, 13, 11, 0, 0), timeProvider.LocalTimeZone);
        CreateArchiveAtLocal(new DateTime(2026, 5, 12, 11, 0, 0), timeProvider.LocalTimeZone);
        CreateArchiveAtLocal(new DateTime(2026, 5, 10, 11, 0, 0), timeProvider.LocalTimeZone);
        CreateArchiveAtLocal(new DateTime(2026, 5, 1, 11, 0, 0), timeProvider.LocalTimeZone);
        CreateArchiveAtLocal(new DateTime(2026, 4, 15, 11, 0, 0), timeProvider.LocalTimeZone);
        CreateArchiveAtLocal(new DateTime(2026, 3, 15, 11, 0, 0), timeProvider.LocalTimeZone);
        CreateArchiveAtLocal(new DateTime(2026, 2, 15, 11, 0, 0), timeProvider.LocalTimeZone);

        var groups = GroupArchives(timeProvider);

        Assert.Equal(
            new[]
            {
                "Today",
                "Yesterday",
                new DateTime(2026, 5, 12).ToString("dddd", CultureInfo.CurrentCulture),
                "Last Week",
                "This Month",
                "Last Month",
                new DateTime(2026, 3, 1).ToString("MMMM yyyy", CultureInfo.CurrentCulture),
                new DateTime(2026, 2, 1).ToString("MMMM yyyy", CultureInfo.CurrentCulture)
            },
            groups.Select(group => group.Heading));
        Assert.Equal(new[] { todayLater, todayEarlier }, groups[0].ArchivePaths);
    }

    [Fact]
    public void TextPreviewRejectsBinaryContentWithNulInDetectionWindow()
    {
        var binary = ArchiveContentProjection.CreateTextPreview([65, 0, 66]);

        Assert.False(binary.IsSupported);
        Assert.False(binary.IsTruncated);
    }

    [Fact]
    public void TextPreviewAtMaximumSizeIsNotTruncated()
    {
        var content = Encoding.UTF8.GetBytes(new string('a', ArchiveContentProjection.MaxPreviewBytes));

        var preview = ArchiveContentProjection.CreateTextPreview(content);

        Assert.True(preview.IsSupported);
        Assert.Equal(ArchiveContentProjection.MaxPreviewBytes, Encoding.UTF8.GetByteCount(preview.Text));
        Assert.False(preview.IsTruncated);
    }

    [Fact]
    public void TextPreviewOneByteOverMaximumIsTruncated()
    {
        var content = Encoding.UTF8.GetBytes(new string('a', ArchiveContentProjection.MaxPreviewBytes + 1));

        var preview = ArchiveContentProjection.CreateTextPreview(content);

        Assert.True(preview.IsSupported);
        Assert.Equal(ArchiveContentProjection.MaxPreviewBytes, Encoding.UTF8.GetByteCount(preview.Text));
        Assert.True(preview.IsTruncated);
    }

    [Fact]
    public void TextPreviewDropsUtf8CharacterStraddlingTruncationBoundary()
    {
        const string multibyteCharacter = "€";
        var content = Encoding.UTF8.GetBytes(
            new string('a', ArchiveContentProjection.MaxPreviewBytes - 1) + multibyteCharacter + "tail");

        var preview = ArchiveContentProjection.CreateTextPreview(content);

        Assert.True(preview.IsSupported);
        Assert.Equal(new string('a', ArchiveContentProjection.MaxPreviewBytes - 1), preview.Text);
        Assert.True(preview.IsTruncated);
    }

    [Fact]
    public void TextPreviewSupportsEmptyFile()
    {
        var preview = ArchiveContentProjection.CreateTextPreview([]);

        Assert.True(preview.IsSupported);
        Assert.Empty(preview.Text);
        Assert.False(preview.IsTruncated);
    }

    [Fact]
    public void TextPreviewSupportsUtf16LittleEndianWithBom()
    {
        const string text = "little endian text";
        var encoding = new UnicodeEncoding(bigEndian: false, byteOrderMark: true, throwOnInvalidBytes: true);
        var content = encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray();

        var preview = ArchiveContentProjection.CreateTextPreview(content);

        Assert.True(preview.IsSupported);
        Assert.Equal(text, preview.Text);
        Assert.False(preview.IsTruncated);
    }

    [Fact]
    public void TextPreviewSupportsUtf16BigEndianWithBom()
    {
        const string text = "big endian text";
        var encoding = new UnicodeEncoding(bigEndian: true, byteOrderMark: true, throwOnInvalidBytes: true);
        var content = encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray();

        var preview = ArchiveContentProjection.CreateTextPreview(content);

        Assert.True(preview.IsSupported);
        Assert.Equal(text, preview.Text);
        Assert.False(preview.IsTruncated);
    }

    [Fact]
    public void TextPreviewTruncatesUtf16AtByteLimitWithoutBreakingSurrogatePair()
    {
        var encoding = new UnicodeEncoding(bigEndian: false, byteOrderMark: true, throwOnInvalidBytes: true);
        var includedCodeUnits = (ArchiveContentProjection.MaxPreviewBytes - encoding.GetPreamble().Length) / 2;
        var prefix = new string('a', includedCodeUnits - 1);
        var content = encoding.GetPreamble()
            .Concat(encoding.GetBytes(prefix + "😀" + "tail"))
            .ToArray();

        var preview = ArchiveContentProjection.CreateTextPreview(content);

        Assert.True(preview.IsSupported);
        Assert.Equal(prefix, preview.Text);
        Assert.False(char.IsHighSurrogate(preview.Text[^1]));
        Assert.False(char.IsLowSurrogate(preview.Text[^1]));
        Assert.True(preview.IsTruncated);
    }

    [Fact]
    public void TextPreviewDetectsUtf32LittleEndianBeforeUtf16()
    {
        const string text = "UTF-32 text";
        var encoding = new UTF32Encoding(bigEndian: false, byteOrderMark: true, throwOnInvalidCharacters: true);
        var content = encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray();

        var preview = ArchiveContentProjection.CreateTextPreview(content);

        Assert.True(preview.IsSupported);
        Assert.Equal(text, preview.Text);
        Assert.False(preview.Text.StartsWith('\uFEFF'));
    }

    [Fact]
    public void TextPreviewStripsUtf8Bom()
    {
        const string text = "UTF-8 text";
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true);
        var content = encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray();

        var preview = ArchiveContentProjection.CreateTextPreview(content);

        Assert.True(preview.IsSupported);
        Assert.Equal(text, preview.Text);
        Assert.False(preview.Text.StartsWith('\uFEFF'));
    }

    [Fact]
    public void TextPreviewStillRejectsBomlessNulHeavyContent()
    {
        var content = new byte[8192];
        for (var index = 0; index < content.Length; index += 2)
        {
            content[index] = (byte)'A';
        }

        var preview = ArchiveContentProjection.CreateTextPreview(content);

        Assert.False(preview.IsSupported);
    }

    [Fact]
    public void TextPreviewRejectsUtf16BomWithOddTrailingByteWithoutThrowing()
    {
        var encoding = new UnicodeEncoding(bigEndian: false, byteOrderMark: true, throwOnInvalidBytes: true);
        var content = encoding.GetPreamble()
            .Concat(encoding.GetBytes("valid"))
            .Append((byte)0x41)
            .ToArray();

        var preview = ArchiveContentProjection.CreateTextPreview(content);

        Assert.False(preview.IsSupported);
        Assert.Empty(preview.Text);
    }

    [Fact]
    public void TextPreviewRejectsNulByteBeyondBinaryDetectionWindow()
    {
        var content = Enumerable.Repeat((byte)'a', 5000).ToArray();
        content[4096 + 100] = 0;

        var preview = ArchiveContentProjection.CreateTextPreview(content);

        Assert.False(preview.IsSupported);
    }

    [Fact]
    public void BuildTreeRejectsLeadingSlash()
    {
        var file = CreateFileEntry("/docs/readme.txt");

        Assert.Throws<InvalidDataException>(() => ArchiveContentProjection.BuildTree(
            [file],
            Array.Empty<DirectoryEntry>()));
    }

    [Fact]
    public void BuildTreeNormalizesBackslashSeparators()
    {
        var file = CreateFileEntry("docs\\nested\\readme.txt");

        var tree = ArchiveContentProjection.BuildTree([file], Array.Empty<DirectoryEntry>());

        var docs = Assert.Single(tree);
        Assert.Equal("docs", docs.Name);
        var nested = Assert.Single(docs.Children);
        Assert.Equal("nested", nested.Name);
        var readme = Assert.Single(nested.Children);
        Assert.Equal("readme.txt", readme.Name);
        Assert.Equal("docs\\nested\\readme.txt", readme.ArchivePath);
    }

    [Fact]
    public void BuildTreeDeduplicatesDuplicatePaths()
    {
        var first = CreateFileEntry("docs/readme.txt");
        var duplicate = CreateFileEntry("docs/readme.txt");

        var tree = ArchiveContentProjection.BuildTree([first, duplicate], Array.Empty<DirectoryEntry>());

        var docs = Assert.Single(tree);
        Assert.Single(docs.Children);
        Assert.Equal("readme.txt", docs.Children[0].Name);
    }

    [Fact]
    public void BuildTreeKeepsDocsAndDocsBakAsSeparateDirectories()
    {
        var files = new[]
        {
            CreateFileEntry("docs/readme.txt"),
            CreateFileEntry("docs.bak/readme.txt")
        };

        var tree = ArchiveContentProjection.BuildTree(files, Array.Empty<DirectoryEntry>());

        Assert.Equal(new[] { "docs", "docs.bak" }, tree.Select(node => node.Name));
        Assert.Single(tree[0].Children);
        Assert.Single(tree[1].Children);
    }

    private static FileEntry CreateFileEntry(string relativePath) => new()
    {
        RelativePath = relativePath,
        Operation = FileOperation.Added,
        FileSize = 1,
        LastModifiedUtc = DateTimeOffset.UnixEpoch,
        Sha256 = "test-hash"
    };

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"eva-archive-content-projection-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private IReadOnlyList<ArchiveDateGroup> GroupArchives(TimeProvider timeProvider) =>
        ArchiveDateGrouping.GroupArchives(Directory.EnumerateFiles(_tempDirectory), timeProvider);

    private string CreateArchiveAtLocal(DateTime localTimestamp, TimeZoneInfo timeZone)
    {
        var localDateTime = DateTime.SpecifyKind(localTimestamp, DateTimeKind.Unspecified);
        var utcTimestamp = TimeZoneInfo.ConvertTimeToUtc(localDateTime, timeZone);
        var fileName = utcTimestamp.ToString("yyyy_MM_dd_HHmmss", CultureInfo.InvariantCulture) + "_full_01.eva";
        var path = Path.Combine(_tempDirectory, fileName);
        File.WriteAllBytes(path, []);
        return path;
    }

    private string CreateArchiveFile(string fileName)
    {
        var path = Path.Combine(_tempDirectory, fileName);
        File.WriteAllBytes(path, []);
        return path;
    }

    private static FixedTimeProvider CreateTimeProvider(DateTime localNow, TimeSpan offset)
    {
        var timeZone = TimeZoneInfo.CreateCustomTimeZone(
            $"EVA-Test-{offset.Ticks}", offset, "EVA test zone", "EVA test zone");
        var localDateTime = DateTime.SpecifyKind(localNow, DateTimeKind.Unspecified);
        var utcNow = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localDateTime, timeZone));
        return new FixedTimeProvider(utcNow, timeZone);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow, TimeZoneInfo localTimeZone) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
        public override TimeZoneInfo LocalTimeZone => localTimeZone;
    }
}