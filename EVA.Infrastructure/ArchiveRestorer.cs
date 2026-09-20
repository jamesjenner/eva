using EVA.Core.Interfaces;
using EVA.Core.Models;

namespace EVA.Infrastructure;

public sealed class ArchiveRestorer : IArchiveRestorer
{
    private readonly IArchiveReader _archiveReader;

    public ArchiveRestorer(IArchiveReader? archiveReader = null)
    {
        _archiveReader = archiveReader ?? new ArchiveReader();
    }

    public async Task<RestoreResult> RestoreAsync(
        string targetArchivePath,
        string restoreDestinationDirectory,
        string password,
        RestoreMode restoreMode,
        bool dryRun = false,
        bool overwriteExisting = false,
        CancellationToken cancellationToken = default)
    {
        var result = new RestoreResult();
        if (string.IsNullOrWhiteSpace(targetArchivePath) || string.IsNullOrWhiteSpace(restoreDestinationDirectory) || string.IsNullOrWhiteSpace(password))
        {
            result.Errors.Add("Archive path, restore destination, and password are required.");
            return result;
        }

        try
        {
            var targetPath = Path.GetFullPath(targetArchivePath);
            var destination = Path.GetFullPath(restoreDestinationDirectory);
            Directory.CreateDirectory(destination);

            var manifests = await LoadChainAsync(targetPath, password, cancellationToken).ConfigureAwait(false);
            foreach (var manifest in manifests)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ApplyDeletedEntries(manifest, destination, result, dryRun);
                await ApplyFileEntriesAsync(manifest, destination, result, dryRun, overwriteExisting, cancellationToken).ConfigureAwait(false);
            }

            result.Success = result.Errors.Count == 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            result.Errors.Add(ex.Message);
            result.Success = false;
        }

        return result;
    }

    private async Task<IReadOnlyList<ArchiveManifest>> LoadChainAsync(
        string targetPath,
        string password,
        CancellationToken cancellationToken)
    {
        var targetManifest = await _archiveReader.ReadManifestAsync(targetPath, password, cancellationToken).ConfigureAwait(false);
        if (targetManifest.ArchiveType == ArchiveType.Full)
        {
            return [targetManifest];
        }

        var directory = Path.GetDirectoryName(targetPath) ?? throw new InvalidOperationException("Archive directory could not be determined.");
        var manifests = new List<(string Path, ArchiveManifest Manifest)>();
        foreach (var archivePath in Directory.EnumerateFiles(directory, "*.eva", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var manifest = await _archiveReader.ReadManifestAsync(archivePath, password, cancellationToken).ConfigureAwait(false);
                if (string.Equals(manifest.ChainId, targetManifest.ChainId, StringComparison.OrdinalIgnoreCase))
                {
                    manifests.Add((archivePath, manifest));
                }
            }
            catch
            {
                // Ignore unrelated or unreadable archives while finding the selected chain.
            }
        }

        var byId = manifests.ToDictionary(item => item.Manifest.ArchiveId, item => item.Manifest, StringComparer.OrdinalIgnoreCase);
        var chain = new List<ArchiveManifest>();
        var current = targetManifest;
        while (true)
        {
            chain.Add(current);
            if (current.ArchiveType == ArchiveType.Full)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(current.ParentArchiveId) || !byId.TryGetValue(current.ParentArchiveId, out current!))
            {
                throw new InvalidOperationException("The selected archive chain does not contain its full backup.");
            }
        }

        chain.Reverse();
        return chain;
    }

    private static void ApplyDeletedEntries(
        ArchiveManifest manifest,
        string destination,
        RestoreResult result,
        bool dryRun)
    {
        foreach (var deleted in manifest.Deleted)
        {
            var path = GetDestinationPath(destination, deleted.RelativePath);
            if (File.Exists(path))
            {
                if (!dryRun)
                {
                    File.Delete(path);
                }
            }
        }
    }

    private static async Task ApplyFileEntriesAsync(
        ArchiveManifest manifest,
        string destination,
        RestoreResult result,
        bool dryRun,
        bool overwriteExisting,
        CancellationToken cancellationToken)
    {
        foreach (var entry in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = GetDestinationPath(destination, entry.RelativePath);
            if (entry.Operation == FileOperation.Deleted)
            {
                if (File.Exists(path))
                {
                    if (!dryRun)
                    {
                        File.Delete(path);
                    }
                }

                continue;
            }

            if (File.Exists(path))
            {
                if (!overwriteExisting)
                {
                    result.FilesToOverwrite.Add(path);
                    continue;
                }
            }

            if (dryRun)
            {
                result.FilesRestored.Add(path);
                continue;
            }

            var parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                Directory.CreateDirectory(parent);
            }

            if (entry.ContentRef is not { ObjectId: var sourcePath } || !File.Exists(sourcePath))
            {
                if (entry.FileSize != 0)
                {
                    result.Errors.Add($"Archive content is unavailable for '{entry.RelativePath}'.");
                    return;
                }

                await using var emptyStream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                result.FilesRestored.Add(path);
                continue;
            }

            File.Copy(sourcePath, path, overwrite: overwriteExisting);
            var actualHash = await ComputeSha256Async(path, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(entry.Sha256) && !string.Equals(actualHash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                result.Errors.Add($"SHA-256 hash verification failed for '{entry.RelativePath}'.");
                return;
            }

            result.FilesRestored.Add(path);
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var hash = await sha256.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string GetDestinationPath(string destination, string relativePath)
    {
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var path = Path.GetFullPath(Path.Combine(destination, normalized));
        if (!path.StartsWith(destination.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(path, destination, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Archive contains a path outside the restore destination.");
        }

        return path;
    }
}
