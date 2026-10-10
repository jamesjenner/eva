using EVA.Core.Models;

namespace EVA.Core.Interfaces;

public interface IPreviewCacheService
{
    Task<PreviewCacheResult> EnsureCachedAsync(
        string archivePath,
        string password,
        CancellationToken cancellationToken = default);

    Task DeleteArchiveAsync(string archiveId, CancellationToken cancellationToken = default);

    void DeleteAll();
}