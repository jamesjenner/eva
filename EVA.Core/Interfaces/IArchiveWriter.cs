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

    Task WriteArchiveAsync(
        ArchiveManifest manifest,
        IReadOnlyCollection<FileEntry> fileEntries,
        List<DirectoryEntry> directoryEntries,
        string destinationPath,
        string password,
        CancellationToken cancellationToken = default)
    {
        return WriteArchiveAsync(manifest, fileEntries, destinationPath, password, cancellationToken);
    }
}
