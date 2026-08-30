using EVA.Core.Models;

namespace EVA.Core.Interfaces;

public interface IRetentionEvaluator
{
    Task ApplyRetentionAsync(string archiveDirectory, CancellationToken cancellationToken = default);
    IReadOnlyCollection<string> GetDeletionCandidates(IEnumerable<ArchiveHeader> archives, RetentionPolicy retentionPolicy, DateTimeOffset currentTimeUtc);
}
