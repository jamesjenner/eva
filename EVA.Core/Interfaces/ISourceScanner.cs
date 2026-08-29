using EVA.Core.Models;

namespace EVA.Core.Interfaces;

public interface ISourceScanner
{
    Task<IReadOnlyCollection<FileEntry>> ScanSourceAsync(string sourceDirectory, CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<DirectoryEntry>> ScanDirectoriesAsync(string sourceDirectory, CancellationToken cancellationToken = default);
}
