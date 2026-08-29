using EVA.Core.Models;

namespace EVA.Core.Interfaces;

public interface IArchiveIndex
{
    Task AddOrUpdateArchiveAsync(ArchiveHeader header, string archivePath, CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<ArchiveHeader>> GetArchivesAsync(string archiveDirectory, CancellationToken cancellationToken = default);
    Task<ArchiveChain> GetChainAsync(string chainId, CancellationToken cancellationToken = default);
    Task RemoveArchiveAsync(string archiveId, CancellationToken cancellationToken = default);
}
