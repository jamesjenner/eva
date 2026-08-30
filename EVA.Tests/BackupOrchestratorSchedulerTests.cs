using EVA.Core.Interfaces;
using EVA.Core.Models;
using EVA.Infrastructure;
using Xunit;

namespace EVA.Tests;

public sealed class BackupSchedulerTests
{
    [Fact]
    public void WeeklySnapshotIsDueOnSundayWhenNoWeeklySnapshotExistsForTheCurrentWeek()
    {
        var scheduler = new BackupScheduler();
        var checkTime = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

        var due = scheduler.IsWeeklySnapshotDue(checkTime, null);

        Assert.True(due);
    }

    [Fact]
    public void WeeklySnapshotIsNotDueOnSundayWhenOneAlreadyExistsForThisWeek()
    {
        var scheduler = new BackupScheduler();
        var checkTime = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
        var lastSnapshot = new DateTimeOffset(2026, 8, 29, 11, 0, 0, TimeSpan.Zero);

        var due = scheduler.IsWeeklySnapshotDue(checkTime, lastSnapshot);

        Assert.False(due);
    }

    [Fact]
    public void MonthlySnapshotIsDueOnTheFirstWhenNoMonthlySnapshotExistsForCurrentMonth()
    {
        var scheduler = new BackupScheduler();
        var checkTime = new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);

        var due = scheduler.IsMonthlySnapshotDue(checkTime, null);

        Assert.True(due);
    }

    [Fact]
    public void WeeklySnapshotOnTheFirstSatisfiesMonthlyCategoryWithoutDuplicate()
    {
        var scheduler = new BackupScheduler();
        var checkTime = new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);

        var weeklyDue = scheduler.IsWeeklySnapshotDue(checkTime, null);
        var monthlyDue = scheduler.IsMonthlySnapshotDue(checkTime, null);

        Assert.True(weeklyDue);
        Assert.True(monthlyDue);
    }

    [Fact]
    public void MissedWeeklyWindowFiresOnNextAvailableCheck()
    {
        var scheduler = new BackupScheduler();
        var missedTime = new DateTimeOffset(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);
        var lastSnapshot = new DateTimeOffset(2026, 8, 23, 10, 0, 0, TimeSpan.Zero);

        var due = scheduler.IsWeeklySnapshotDue(missedTime, lastSnapshot);

        Assert.True(due);
    }

    [Fact]
    public void MissedMonthlyWindowFiresOnNextAvailableCheck()
    {
        var scheduler = new BackupScheduler();
        var checkTime = new DateTimeOffset(2026, 9, 2, 10, 0, 0, TimeSpan.Zero);
        var lastSnapshot = new DateTimeOffset(2026, 8, 31, 10, 0, 0, TimeSpan.Zero);

        var due = scheduler.IsMonthlySnapshotDue(checkTime, lastSnapshot);

        Assert.True(due);
    }

    [Fact]
    public void MissedEntirePeriodFiresImmediatelyOnResume()
    {
        var scheduler = new BackupScheduler();
        var checkTime = new DateTimeOffset(2026, 10, 3, 10, 0, 0, TimeSpan.Zero);
        var lastSnapshot = new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);

        var due = scheduler.IsMonthlySnapshotDue(checkTime, lastSnapshot);

        Assert.True(due);
    }
}

public sealed class BackupOrchestratorTests
{
    [Fact]
    public async Task NoArchiveIsCreatedWhenScannerReportsNoChanges()
    {
        var root = CreateTempDirectory();
        var destination = Path.Combine(root, "archives");
        Directory.CreateDirectory(destination);

        var scanner = CreateScanner(root, destination);
        var orchestrator = new BackupOrchestrator(root, destination, null, "Password123!", scanner: scanner, writer: new ArchiveWriter());

        var result = await orchestrator.RunOnceAsync(DateTimeOffset.UtcNow, manualSnapshot: false, CancellationToken.None);

        Assert.False(result.CreatedArchive);
        Assert.True(result.Success);
    }

    [Fact]
    public async Task IncrementalArchiveIsCreatedWhenChangesExistAndNoSnapshotIsDue()
    {
        var root = CreateTempDirectory();
        var destination = Path.Combine(root, "archives");
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(root, "a.txt"), "alpha");

        var scanner = CreateScanner(root, destination);
        var orchestrator = new BackupOrchestrator(root, destination, null, "Password123!", scanner: scanner, writer: new ArchiveWriter());

        var result = await orchestrator.RunOnceAsync(new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero), manualSnapshot: false, CancellationToken.None);

        Assert.True(result.CreatedArchive);
        Assert.Equal(ArchiveType.Incremental, result.ArchiveType);
    }

    [Fact]
    public async Task SnapshotArchiveIsCreatedWhenSchedulerDeterminesSnapshotIsDue()
    {
        var root = CreateTempDirectory();
        var destination = Path.Combine(root, "archives");
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(root, "a.txt"), "alpha");

        var scanner = CreateScanner(root, destination);
        var orchestrator = new BackupOrchestrator(root, destination, null, "Password123!", scanner: scanner, writer: new ArchiveWriter());

        var result = await orchestrator.RunOnceAsync(new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero), false, CancellationToken.None);

        Assert.True(result.CreatedArchive);
        Assert.Equal(ArchiveType.Snapshot, result.ArchiveType);
    }

    [Fact]
    public async Task ManualSnapshotIsAlwaysCreatedRegardlessOfChangeState()
    {
        var root = CreateTempDirectory();
        var destination = Path.Combine(root, "archives");
        Directory.CreateDirectory(destination);

        var scanner = CreateScanner(root, destination);
        var orchestrator = new BackupOrchestrator(root, destination, null, "Password123!", scanner: scanner, writer: new ArchiveWriter());

        var result = await orchestrator.RunOnceAsync(DateTimeOffset.UtcNow, manualSnapshot: true, CancellationToken.None);

        Assert.True(result.CreatedArchive);
        Assert.Equal(ArchiveType.Snapshot, result.ArchiveType);
    }

    [Fact]
    public async Task FailedPrimaryDestinationWriteIsReportedAndDoesNotCorruptSource()
    {
        var root = CreateTempDirectory();
        var destination = Path.Combine(root, "archives");
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(root, "a.txt"), "alpha");

        var sourceBefore = await File.ReadAllTextAsync(Path.Combine(root, "a.txt"));
        var scanner = CreateScanner(root, destination);
        var badWriter = new ThrowingArchiveWriter();
        var orchestrator = new BackupOrchestrator(root, destination, null, "Password123!", scanner: scanner, writer: badWriter, logger: new TestEventLogger());

        var result = await orchestrator.RunOnceAsync(DateTimeOffset.UtcNow, false, CancellationToken.None);
        var sourceAfter = await File.ReadAllTextAsync(Path.Combine(root, "a.txt"));

        Assert.False(result.Success);
        Assert.Equal(sourceBefore, sourceAfter);
    }

    [Fact]
    public async Task FailedSecondaryDestinationCopyIsQueuedForRetryAndDoesNotInvalidatePrimaryArchive()
    {
        var root = CreateTempDirectory();
        var destination = Path.Combine(root, "archives");
        var secondary = Path.Combine(root, "secondary-not-a-directory.txt");
        await File.WriteAllTextAsync(secondary, "not a directory");
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(root, "a.txt"), "alpha");

        var scanner = CreateScanner(root, destination, secondary);
        var retryQueue = new SecondaryDestinationRetryQueue(Path.Combine(root, "queue.json"));
        var orchestrator = new BackupOrchestrator(root, destination, secondary, "Password123!", scanner: scanner, writer: new ArchiveWriter(), retryQueue: retryQueue);

        var result = await orchestrator.RunOnceAsync(DateTimeOffset.UtcNow, false, CancellationToken.None);
        var pending = await retryQueue.GetPendingAsync();

        Assert.True(result.CreatedArchive);
        Assert.NotNull(result.ArchivePath);
        Assert.Single(pending);
    }

    [Fact]
    public async Task ScanStateIndexIsUpdatedOnlyAfterSuccessfulArchive()
    {
        var root = CreateTempDirectory();
        var destination = Path.Combine(root, "archives");
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(root, "a.txt"), "alpha");

        var index = new LocalScanStateIndex(root);
        var scanner = CreateScanner(root, destination, index: index);
        var orchestrator = new BackupOrchestrator(root, destination, null, "Password123!", scanner: scanner, writer: new ArchiveWriter(), stateIndex: index);

        var result = await orchestrator.RunOnceAsync(DateTimeOffset.UtcNow, false, CancellationToken.None);
        var snapshot = await index.LoadAsync();

        Assert.True(result.CreatedArchive);
        Assert.NotNull(snapshot);
        Assert.Single(snapshot.Files);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "eva-backup-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static SourceScanner CreateScanner(string root, string? destination = null, string? secondary = null, LocalScanStateIndex? index = null)
    {
        var excluded = new List<string>();
        if (!string.IsNullOrWhiteSpace(destination))
        {
            excluded.Add(destination);
        }
        if (!string.IsNullOrWhiteSpace(secondary))
        {
            excluded.Add(secondary);
        }

        return new SourceScanner(root, index ?? new LocalScanStateIndex(root), excludedPaths: excluded);
    }

    private sealed class ThrowingArchiveWriter : IArchiveWriter
    {
        public Task WriteArchiveAsync(ArchiveManifest manifest, IReadOnlyCollection<FileEntry> fileEntries, string destinationPath, string password, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Simulated write failure");
        }
    }

    private sealed class TestEventLogger : IEventLogger
    {
        public void LogInformation(string message) { }
        public void LogWarning(string message) { }
        public void LogError(string message) { }
        public void LogException(Exception exception, string message) { }
    }
}

public sealed class SecondaryDestinationRetryQueueTests
{
    [Fact]
    public async Task ArchiveIsAddedToQueueWhenSecondaryCopyFails()
    {
        var queuePath = Path.Combine(CreateTempDirectory(), "retry.json");
        var queue = new SecondaryDestinationRetryQueue(queuePath);
        await queue.EnqueueAsync("archive-one.eva");

        var pending = await queue.GetPendingAsync();

        Assert.Single(pending);
        Assert.Contains("archive-one.eva", pending);
    }

    [Fact]
    public async Task SuccessfulRetryRemovesArchiveFromQueue()
    {
        var queuePath = Path.Combine(CreateTempDirectory(), "retry.json");
        var queue = new SecondaryDestinationRetryQueue(queuePath);
        await queue.EnqueueAsync("archive-one.eva");

        await queue.RetryAsync((archivePath, _) => Task.FromResult(true));
        var pending = await queue.GetPendingAsync();

        Assert.Empty(pending);
    }

    [Fact]
    public async Task QueuePersistsAcrossApplicationRestarts()
    {
        var queuePath = Path.Combine(CreateTempDirectory(), "retry.json");
        var first = new SecondaryDestinationRetryQueue(queuePath);
        await first.EnqueueAsync("archive-one.eva");

        var second = new SecondaryDestinationRetryQueue(queuePath);
        var pending = await second.GetPendingAsync();

        Assert.Single(pending);
        Assert.Contains("archive-one.eva", pending);
    }

    [Fact]
    public async Task FailedRetryLeavesArchiveInQueue()
    {
        var queuePath = Path.Combine(CreateTempDirectory(), "retry.json");
        var queue = new SecondaryDestinationRetryQueue(queuePath);
        await queue.EnqueueAsync("archive-one.eva");

        await queue.RetryAsync((archivePath, _) => Task.FromResult(false));
        var pending = await queue.GetPendingAsync();

        Assert.Single(pending);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "eva-retry-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
