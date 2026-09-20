using EVA.Core.Interfaces;
using EVA.Core.Models;

namespace EVA.Infrastructure;

public sealed class RetentionEvaluator : IRetentionEvaluator
{
    public async Task ApplyRetentionAsync(string archiveDirectory, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(archiveDirectory))
        {
            throw new ArgumentException("Archive directory is required.", nameof(archiveDirectory));
        }

        if (!Directory.Exists(archiveDirectory))
        {
            return;
        }

        var files = Directory.GetFiles(archiveDirectory, "*.eva", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();

        var archiveHeaders = new List<ArchiveHeader>();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                archiveHeaders.Add(await new ArchiveReader().ReadHeaderAsync(file, cancellationToken).ConfigureAwait(false));
            }
            catch
            {
                // Ignore unreadable or malformed archive files to avoid deleting a file we cannot inspect.
            }
        }

        var candidates = GetDeletionCandidates(archiveHeaders, new RetentionPolicy(), DateTimeOffset.UtcNow);
        var candidateIds = new HashSet<string>(candidates, StringComparer.OrdinalIgnoreCase);

        foreach (var archive in archiveHeaders)
        {
            if (!candidateIds.Contains(archive.ArchiveId))
            {
                continue;
            }

            var match = files.FirstOrDefault(path => string.Equals(Path.GetFileNameWithoutExtension(path), archive.ArchiveId, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(match) && File.Exists(match))
            {
                File.Delete(match);
            }
        }
    }

    public IReadOnlyCollection<string> GetDeletionCandidates(IEnumerable<ArchiveHeader> archives, RetentionPolicy retentionPolicy, DateTimeOffset currentTimeUtc)
    {
        if (archives is null)
        {
            return Array.Empty<string>();
        }

        var lists = archives
            .Where(a => a is not null && !string.IsNullOrWhiteSpace(a.ArchiveId))
            .ToList();

        if (lists.Count == 0)
        {
            return Array.Empty<string>();
        }

        var policy = retentionPolicy ?? new RetentionPolicy();
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var chainGroup in lists.GroupBy(a => a.ChainId, StringComparer.OrdinalIgnoreCase))
        {
            var archiveMap = chainGroup.ToDictionary(a => a.ArchiveId, StringComparer.OrdinalIgnoreCase);
            var retainedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var expiredIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var archive in chainGroup)
            {
                if (IsRetainedByPolicy(archive, policy, currentTimeUtc))
                {
                    retainedIds.Add(archive.ArchiveId);
                }
                else
                {
                    expiredIds.Add(archive.ArchiveId);
                }
            }

            var requiredIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var retainedId in retainedIds)
            {
                CollectRequiredAncestors(retainedId, archiveMap, requiredIds);
            }

            foreach (var expiredId in expiredIds)
            {
                if (!requiredIds.Contains(expiredId))
                {
                    candidates.Add(expiredId);
                }
            }
        }

        return candidates.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void CollectRequiredAncestors(string archiveId, IReadOnlyDictionary<string, ArchiveHeader> archiveMap, ISet<string> requiredIds)
    {
        if (string.IsNullOrWhiteSpace(archiveId) || !archiveMap.TryGetValue(archiveId, out var archive))
        {
            return;
        }

        if (!requiredIds.Add(archive.ArchiveId))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(archive.ParentArchiveId))
        {
            CollectRequiredAncestors(archive.ParentArchiveId, archiveMap, requiredIds);
        }
    }

    private static bool IsRetainedByPolicy(ArchiveHeader archive, RetentionPolicy policy, DateTimeOffset currentTimeUtc)
    {
        if (archive.ArchiveType == ArchiveType.Incremental)
        {
            var age = currentTimeUtc - archive.CreatedUtc;
            return age.TotalDays <= policy.IncrementalRetentionDays;
        }

        if (archive.ArchiveType != ArchiveType.Full)
        {
            return false;
        }

        if (IsMonthlySnapshot(archive.CreatedUtc))
        {
            return policy.MonthlySnapshotRetentionDays is null || (currentTimeUtc - archive.CreatedUtc).TotalDays <= policy.MonthlySnapshotRetentionDays.Value;
        }

        var ageInDays = (currentTimeUtc - archive.CreatedUtc).TotalDays;
        return ageInDays <= policy.WeeklySnapshotRetentionDays;
    }

    private static bool IsMonthlySnapshot(DateTimeOffset createdUtc)
    {
        return createdUtc.Day == 1;
    }
}
