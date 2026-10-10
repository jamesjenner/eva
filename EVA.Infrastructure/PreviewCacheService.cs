using EVA.Core.Interfaces;
using EVA.Core.Models;

namespace EVA.Infrastructure;

public sealed class PreviewCacheService : IPreviewCacheService
{
    public const long MaxArchiveSizeBytes = 512L * 1024 * 1024;

    private readonly string _cacheRoot;
    private readonly IArchiveReader _archiveReader;
    private readonly IArchivePayloadExtractor _payloadExtractor;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PreviewCacheService(
        string cacheRoot,
        IArchiveReader? archiveReader = null,
        IArchivePayloadExtractor? payloadExtractor = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        _cacheRoot = Path.GetFullPath(cacheRoot);

        var reader = archiveReader ?? new ArchiveReader();
        _archiveReader = reader;
        _payloadExtractor = payloadExtractor ?? reader as IArchivePayloadExtractor
            ?? throw new ArgumentException("The archive reader must support payload extraction.", nameof(archiveReader));
    }

    public async Task<PreviewCacheResult> EnsureCachedAsync(
        string archivePath,
        string password,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        cancellationToken.ThrowIfCancellationRequested();

        var archiveInfo = new FileInfo(archivePath);
        if (archiveInfo.Exists && archiveInfo.Length > MaxArchiveSizeBytes)
        {
            return new PreviewCacheResult(string.Empty, null, Array.Empty<string>(), false, true);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var header = await _archiveReader.ReadHeaderAsync(archivePath, cancellationToken).ConfigureAwait(false);
            ValidateArchiveId(header.ArchiveId);
            var cacheDirectory = Path.Combine(_cacheRoot, header.ArchiveId);
            if (Directory.Exists(cacheDirectory))
            {
                return new PreviewCacheResult(header.ArchiveId, cacheDirectory, Array.Empty<string>(), true, false);
            }

            Directory.CreateDirectory(_cacheRoot);
            var stagingDirectory = Path.Combine(_cacheRoot, $".staging-{Guid.NewGuid():N}");
            Directory.CreateDirectory(stagingDirectory);
            try
            {
                var extraction = await _payloadExtractor.ExtractFilesAsync(
                    archivePath,
                    password,
                    stagingDirectory,
                    cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                ValidateArchiveId(extraction.ArchiveId);
                if (!string.Equals(extraction.ArchiveId, header.ArchiveId, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("Archive manifest identity does not match its header.");
                }

                if (Directory.Exists(cacheDirectory))
                {
                    Directory.Delete(stagingDirectory, recursive: true);
                    return new PreviewCacheResult(header.ArchiveId, cacheDirectory, extraction.SkippedEntries, true, false);
                }

                Directory.Move(stagingDirectory, cacheDirectory);
                return new PreviewCacheResult(header.ArchiveId, cacheDirectory, extraction.SkippedEntries, false, false);
            }
            catch
            {
                DeleteDirectoryIfExists(stagingDirectory);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteArchiveAsync(string archiveId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveId);
        ValidateArchiveId(archiveId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DeleteDirectoryIfExists(Path.Combine(_cacheRoot, archiveId));
        }
        finally
        {
            _gate.Release();
        }
    }

    public void DeleteAll()
    {
        DeleteDirectoryIfExists(_cacheRoot);
    }

    private static void ValidateArchiveId(string archiveId)
    {
        if (string.IsNullOrWhiteSpace(archiveId) || archiveId is "." or ".." ||
            archiveId.IndexOfAny(['/', '\\']) >= 0 || archiveId.EndsWith('.') || archiveId.EndsWith(' ') ||
            archiveId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || archiveId.Any(char.IsControl))
        {
            throw new InvalidDataException("Archive ID is not a safe cache directory name.");
        }
    }

    private static void DeleteDirectoryIfExists(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}