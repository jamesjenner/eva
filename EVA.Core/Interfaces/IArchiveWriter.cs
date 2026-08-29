using EVA.Core.Models;

namespace EVA.Core.Interfaces;

public interface IArchiveWriter
{
    Task WriteArchiveAsync(
        ArchiveManifest manifest,
        IReadOnlyCollection<FileEntry> fileEntries,
        string destinationPath,
        string password,
        CancellationToken cancellationToken = default);
}
