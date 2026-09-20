using EVA.Core.Models;

namespace EVA.Core.Interfaces;

public interface IArchiveReader
{
    Task<ArchiveHeader> ReadHeaderAsync(string archivePath, CancellationToken cancellationToken = default);
    Task<ArchiveManifest> ReadManifestAsync(string archivePath, string password, CancellationToken cancellationToken = default);
    Task<byte[]> ReadFileContentAsync(string archivePath, string relativePath, string password, CancellationToken cancellationToken = default);
    Task<bool> ValidateArchiveAsync(string archivePath, string password, CancellationToken cancellationToken = default);
}
