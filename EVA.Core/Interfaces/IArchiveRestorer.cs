using EVA.Core.Models;

namespace EVA.Core.Interfaces;

public interface IArchiveRestorer
{
    Task<RestoreResult> RestoreAsync(
        string targetArchivePath,
        string restoreDestinationDirectory,
        string password,
        RestoreMode restoreMode,
        bool dryRun = false,
        bool overwriteExisting = false,
        CancellationToken cancellationToken = default);
}
