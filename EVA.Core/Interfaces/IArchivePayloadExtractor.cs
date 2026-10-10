using EVA.Core.Models;

namespace EVA.Core.Interfaces;

public interface IArchivePayloadExtractor
{
    Task<ArchiveExtractionResult> ExtractFilesAsync(
        string archivePath,
        string password,
        string destinationDirectory,
        CancellationToken cancellationToken = default);
}