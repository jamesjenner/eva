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
            var replayedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var overwritePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var archive in manifests)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ApplyDirectoryEntries(archive.Manifest, destination, result, dryRun);
                ApplyDeletedEntries(archive.Manifest, destination, replayedPaths, result, dryRun);
                await ApplyFileEntriesAsync(archive.Path, archive.Manifest, destination, replayedPaths, overwritePaths, result, dryRun, password, overwriteExisting, cancellationToken).ConfigureAwait(false);
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

    private async Task<IReadOnlyList<ArchiveFile>> LoadChainAsync(
        string targetPath,
        string password,
        CancellationToken cancellationToken)
    {
        var targetManifest = await _archiveReader.ReadManifestAsync(targetPath, password, cancellationToken).ConfigureAwait(false);
        if (targetManifest.ArchiveType == ArchiveType.Full)
        {
            return [new ArchiveFile(targetPath, targetManifest)];
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

        var byId = manifests.ToDictionary(item => item.Manifest.ArchiveId, item => new ArchiveFile(item.Path, item.Manifest), StringComparer.OrdinalIgnoreCase);
        var chain = new List<ArchiveFile>();
        var currentPath = targetPath;
        var current = targetManifest;
        while (true)
        {
            chain.Add(new ArchiveFile(currentPath, current));
            if (current.ArchiveType == ArchiveType.Full)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(current.ParentArchiveId) || !byId.TryGetValue(current.ParentArchiveId, out var parent))
            {
                throw new InvalidOperationException("The selected archive chain does not contain its full backup.");
            }

            currentPath = parent.Path;
            current = parent.Manifest;
        }

        chain.Reverse();
        return chain;
    }

    private static void ApplyDeletedEntries(
        ArchiveManifest manifest,
        string destination,
        HashSet<string> replayedPaths,
        RestoreResult result,
        bool dryRun)
    {
        foreach (var deleted in manifest.Deleted)
        {
            var path = GetDestinationPath(destination, deleted.RelativePath);
            if (File.Exists(path))
            {
                if (!dryRun || replayedPaths.Contains(path))
                {
                    if (!dryRun || replayedPaths.Contains(path))
                    {
                        File.Delete(path);
                    }
                }

                replayedPaths.Remove(path);
            }
        }
    }

    private static void ApplyDirectoryEntries(
        ArchiveManifest manifest,
        string destination,
        RestoreResult result,
        bool dryRun)
    {
        foreach (var entry in manifest.Directories)
        {
            if (!entry.Exists)
            {
                continue;
            }

            var path = GetDestinationPath(destination, entry.RelativePath);
            if (!dryRun)
            {
                Directory.CreateDirectory(path);
            }

            result.DirectoriesRestored.Add(path);
        }
    }

    private async Task ApplyFileEntriesAsync(
        string archivePath,
        ArchiveManifest manifest,
        string destination,
        HashSet<string> replayedPaths,
        HashSet<string> overwritePaths,
        RestoreResult result,
        bool dryRun,
        string password,
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
                    if (!dryRun || replayedPaths.Contains(path))
                    {
                        File.Delete(path);
                    }

                    replayedPaths.Remove(path);
                }

                continue;
            }

            if (File.Exists(path))
            {
                if (!overwriteExisting && !replayedPaths.Contains(path))
                {
                    if (overwritePaths.Add(path))
                    {
                        result.FilesToOverwrite.Add(path);
                    }
                    continue;
                }
            }

            if (dryRun)
            {
                result.FilesRestored.Add(path);
                replayedPaths.Add(path);
                continue;
            }

            var parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                Directory.CreateDirectory(parent);
            }

            byte[] content;
            try
            {
                content = await _archiveReader.ReadFileContentAsync(archivePath, entry.RelativePath, password, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Could not read archive content for '{entry.RelativePath}': {ex.Message}");
                return;
            }

            await File.WriteAllBytesAsync(path, content, cancellationToken).ConfigureAwait(false);
            var actualHash = await ComputeSha256Async(path, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(entry.Sha256) && !string.Equals(actualHash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                result.Errors.Add($"SHA-256 hash verification failed for '{entry.RelativePath}'.");
                return;
            }

            result.FilesRestored.Add(path);
            replayedPaths.Add(path);
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

    private sealed record ArchiveFile(string Path, ArchiveManifest Manifest);
}
