using EVA.Core.Models;
using EVA.Infrastructure;
using Xunit;

namespace EVA.Tests;

public sealed class RetentionEvaluatorTests
{
    [Fact]
    public void IncrementalArchiveOutsideRetentionAndNotRequiredIsReturnedForDeletion()
    {
        var now = new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
        var archives = new[]
        {
            CreateArchive("inc-old", "chain-1", ArchiveType.Incremental, now.AddDays(-10))
        };

        var evaluator = new RetentionEvaluator();
        var result = evaluator.GetDeletionCandidates(archives, CreatePolicy(), now);

        Assert.Single(result);
        Assert.NotNull(result.Single());
    }

    [Fact]
    public void IncrementalArchiveOutsideRetentionButRequiredAsParentIsNotReturnedForDeletion()
    {
        var now = new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
        var oldParent = CreateArchive("snapshot-root", "chain-1", ArchiveType.Snapshot, now.AddDays(-30));
        var retainedChild = CreateArchive("retained-child", "chain-1", ArchiveType.Incremental, now.AddDays(-2), parentArchiveId: oldParent.ArchiveId);
        var archives = new[] { oldParent, retainedChild };

        var evaluator = new RetentionEvaluator();
        var result = evaluator.GetDeletionCandidates(archives, CreatePolicy(), now);

        Assert.Empty(result);
    }

    [Fact]
    public void WeeklySnapshotOutsideRetentionAndNotRequiredIsReturnedForDeletion()
    {
        var now = new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
        var archives = new[]
        {
            CreateArchive("week-old", "chain-1", ArchiveType.Snapshot, now.AddDays(-430), snapshotDay: new DateTimeOffset(2025, 9, 14, 12, 0, 0, TimeSpan.Zero))
        };

        var evaluator = new RetentionEvaluator();
        var result = evaluator.GetDeletionCandidates(archives, CreatePolicy(), now);

        Assert.Single(result);
        Assert.NotNull(result.Single());
    }

    [Fact]
    public void MonthlySnapshotIsNeverReturnedForDeletionRegardlessOfAge()
    {
        var now = new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
        var archives = new[]
        {
            CreateArchive("month-old", "chain-1", ArchiveType.Snapshot, now.AddDays(-400), snapshotDay: new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero))
        };

        var evaluator = new RetentionEvaluator();
        var result = evaluator.GetDeletionCandidates(archives, CreatePolicy(), now);

        Assert.Empty(result);
    }

    [Fact]
    public void ArchiveThatIsBothWeeklyAndMonthlyIsRetainedUnderMonthlyPolicy()
    {
        var now = new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
        var archives = new[]
        {
            CreateArchive("month-week", "chain-1", ArchiveType.Snapshot, now.AddDays(-200), snapshotDay: new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero))
        };

        var evaluator = new RetentionEvaluator();
        var result = evaluator.GetDeletionCandidates(archives, CreatePolicy(), now);

        Assert.Empty(result);
    }

    [Fact]
    public void DeletingProposedArchiveWouldNotLeaveAnyRetainedRestorePointUnrestorable()
    {
        var now = new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
        var snapshot = CreateArchive("snapshot-retained", "chain-1", ArchiveType.Snapshot, now.AddDays(-2), snapshotDay: new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero));
        var delta = CreateArchive("delta-required", "chain-1", ArchiveType.Incremental, now.AddDays(-1), parentArchiveId: snapshot.ArchiveId);
        var expired = CreateArchive("expired-older", "chain-1", ArchiveType.Incremental, now.AddDays(-30), parentArchiveId: snapshot.ArchiveId);

        var evaluator = new RetentionEvaluator();
        var result = evaluator.GetDeletionCandidates(new[] { snapshot, delta, expired }, CreatePolicy(), now);

        Assert.Single(result);
        Assert.Equal("expired-older", result.Single());
    }

    [Fact]
    public void ArchivesFromOlderChainAreEvaluatedIndependentlyOnceNewChainExists()
    {
        var now = new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
        var oldSnapshot = CreateArchive("old-snapshot", "chain-1", ArchiveType.Snapshot, now.AddDays(-430), snapshotDay: new DateTimeOffset(2025, 9, 14, 12, 0, 0, TimeSpan.Zero));
        var newSnapshot = CreateArchive("new-snapshot", "chain-2", ArchiveType.Snapshot, now.AddDays(-2), snapshotDay: new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero));

        var evaluator = new RetentionEvaluator();
        var result = evaluator.GetDeletionCandidates(new[] { oldSnapshot, newSnapshot }, CreatePolicy(), now);

        Assert.Single(result);
        Assert.Equal("old-snapshot", result.Single());
    }

    [Fact]
    public void EmptyArchiveListReturnsEmptyDeletionCandidateList()
    {
        var evaluator = new RetentionEvaluator();
        var result = evaluator.GetDeletionCandidates(Array.Empty<ArchiveHeader>(), CreatePolicy(), DateTimeOffset.UtcNow);

        Assert.Empty(result);
    }

    [Fact]
    public void DefaultRetentionPolicyReturnsCorrectCandidatesForMixedArchiveSet()
    {
        var now = new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
        var oldIncremental = CreateArchive("old-incremental", "chain-1", ArchiveType.Incremental, now.AddDays(-20));
        var recentIncremental = CreateArchive("recent-incremental", "chain-1", ArchiveType.Incremental, now.AddDays(-2));
        var oldWeekly = CreateArchive("old-weekly", "chain-2", ArchiveType.Snapshot, now.AddDays(-430), snapshotDay: new DateTimeOffset(2025, 9, 14, 12, 0, 0, TimeSpan.Zero));
        var monthSnapshot = CreateArchive("monthly", "chain-3", ArchiveType.Snapshot, now.AddDays(-200), snapshotDay: new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero));

        var evaluator = new RetentionEvaluator();
        var result = evaluator.GetDeletionCandidates(new[] { oldIncremental, recentIncremental, oldWeekly, monthSnapshot }, new RetentionPolicy(), now);

        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        Assert.Contains("old-incremental", result);
        Assert.Contains("old-weekly", result);
    }

    private static RetentionPolicy CreatePolicy()
    {
        return new RetentionPolicy
        {
            IncrementalRetentionDays = 7,
            WeeklySnapshotRetentionDays = 365,
            MonthlySnapshotRetentionDays = null
        };
    }

    private static ArchiveHeader CreateArchive(
        string archiveId,
        string chainId,
        ArchiveType archiveType,
        DateTimeOffset createdUtc,
        string? parentArchiveId = null,
        DateTimeOffset? snapshotDay = null)
    {
        var finalCreatedUtc = snapshotDay ?? createdUtc;

        return new ArchiveHeader
        {
            ArchiveId = archiveId,
            ChainId = chainId,
            ArchiveType = archiveType,
            ParentArchiveId = parentArchiveId,
            CreatedUtc = finalCreatedUtc,
            SourceId = "source-1"
        };
    }
}
