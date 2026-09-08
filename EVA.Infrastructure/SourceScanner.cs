using System.Security.Cryptography;
using System.Text;
using EVA.Core.Interfaces;
using EVA.Core.Models;

namespace EVA.Infrastructure;

public sealed class SourceScanner : ISourceScanner
{
    private const int StabilityWaitMilliseconds = 2000;
    private const int MaxStabilityChecks = 3;

    private readonly string _configuredSourceDirectory;
    private readonly LocalScanStateIndex _stateIndex;
    private readonly TimeSpan _verificationInterval;
    private readonly IEventLogger? _logger;
    private readonly HashSet<string> _excludedPaths;

    public SourceScanner(
        string configuredSourceDirectory,
        LocalScanStateIndex? stateIndex = null,
        TimeSpan? verificationInterval = null,
        IEventLogger? logger = null,
        IEnumerable<string>? excludedPaths = null)
    {
        if (string.IsNullOrWhiteSpace(configuredSourceDirectory))
        {
            throw new ArgumentException("Configured source directory is required.", nameof(configuredSourceDirectory));
        }

        _configuredSourceDirectory = Path.GetFullPath(configuredSourceDirectory);
        _stateIndex = stateIndex ?? new LocalScanStateIndex(_configuredSourceDirectory);
        _verificationInterval = verificationInterval ?? TimeSpan.FromHours(24);
        _logger = logger;
        _excludedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (excludedPaths is not null)
        {
            foreach (var excludedPath in excludedPaths)
            {
                if (!string.IsNullOrWhiteSpace(excludedPath))
                {
                    _excludedPaths.Add(Path.GetFullPath(excludedPath));
                }
            }
        }
    }

    public async Task<ScanResult> ScanAsync(string sourceDirectory, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceDirectory))
        {
            throw new ArgumentException("Source directory is required.", nameof(sourceDirectory));
        }

        var root = Path.GetFullPath(sourceDirectory);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Source directory not found: {root}");
        }

        var previousState = await _stateIndex.LoadAsync(cancellationToken).ConfigureAwait(false);
        var result = new ScanResult();
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var filePath in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsExcludedPath(filePath, root))
            {
                continue;
            }

            var relativePath = GetRelativePath(root, filePath);
            seenFiles.Add(relativePath);

            if (!TryReadFile(filePath, out var fileInfo))
            {
                result.UnreadableFiles.Add(relativePath);
                _logger?.LogError($"Unreadable source file: {relativePath}");
                continue;
            }

            if (previousState.Files.TryGetValue(relativePath, out var priorState))
            {
                var currentLastModifiedUtc = NormalizeUtcTimestamp(fileInfo.LastWriteTimeUtc);
                var metadataChanged = priorState.FileSize != fileInfo.Length || priorState.LastModifiedUtc != currentLastModifiedUtc;
                var requiresFullVerification = RequiresFullVerification(priorState.LastFullVerificationUtc, DateTimeOffset.UtcNow, _verificationInterval);

                if (!metadataChanged && !requiresFullVerification)
                {
                    result.StableFiles.Add(CreateFileEntry(relativePath, fileInfo, priorState.Sha256));
                    continue;
                }

                if (!await WaitForFileStabilityAsync(filePath, cancellationToken).ConfigureAwait(false))
                {
                    result.UnstableFiles.Add(CreateFileEntry(relativePath, fileInfo, priorState.Sha256));
                    _logger?.LogWarning($"File could not be hashed, treating as unstable: {relativePath}");
                    continue;
                }

                var currentHash = ComputeSha256(filePath);
                if (currentHash is null)
                {
                    result.UnstableFiles.Add(CreateFileEntry(relativePath, fileInfo, priorState.Sha256));
                    _logger?.LogWarning($"File could not be hashed, treating as unstable: {relativePath}");
                    continue;
                }

                if (!metadataChanged && currentHash == priorState.Sha256)
                {
                    result.StableFiles.Add(CreateFileEntry(relativePath, fileInfo, currentHash));
                    continue;
                }

                if (currentHash == priorState.Sha256)
                {
                    result.StableFiles.Add(CreateFileEntry(relativePath, fileInfo, currentHash));
                    continue;
                }

                result.ConfirmedChanges.Add(new FileEntry
                {
                    RelativePath = relativePath,
                    Operation = FileOperation.Modified,
                    FileSize = fileInfo.Length,
                    LastModifiedUtc = NormalizeUtcTimestamp(fileInfo.LastWriteTimeUtc),
                    Sha256 = currentHash
                });
                
                continue;
            }

            if (!await WaitForFileStabilityAsync(filePath, cancellationToken).ConfigureAwait(false))
            {
                result.UnstableFiles.Add(CreateFileEntry(relativePath, fileInfo));
                continue;
            }

            var sha256 = ComputeSha256(filePath);
            if (sha256 is null)
            {
                result.UnstableFiles.Add(CreateFileEntry(relativePath, fileInfo));
                continue;
            }
            result.ConfirmedChanges.Add(new FileEntry
            {
                RelativePath = relativePath,
                Operation = FileOperation.Added,
                FileSize = fileInfo.Length,
                LastModifiedUtc = fileInfo.LastWriteTimeUtc,
                Sha256 = sha256
            });        
        }

        foreach (var knownEntry in previousState.Files)
        {
            if (!seenFiles.Contains(knownEntry.Key))
            {
                result.ConfirmedChanges.Add(new FileEntry
                {
                    RelativePath = knownEntry.Key,
                    Operation = FileOperation.Deleted,
                    FileSize = knownEntry.Value.FileSize,
                    LastModifiedUtc = knownEntry.Value.LastModifiedUtc,
                    Sha256 = knownEntry.Value.Sha256
                });
            }
        }

        foreach (var directoryPath in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsExcludedPath(directoryPath, root))
            {
                continue;
            }

            var relativePath = GetRelativePath(root, directoryPath);
            seenDirectories.Add(relativePath);

            if (!Directory.EnumerateFileSystemEntries(directoryPath).Any())
            {
                result.EmptyDirectories.Add(new DirectoryEntry
                {
                    RelativePath = relativePath,
                    Operation = FileOperation.Added,
                    Exists = true,
                    LastModifiedUtc = Directory.GetLastWriteTimeUtc(directoryPath)
                });
            }
        }

        if (!Directory.EnumerateFileSystemEntries(root).Any())
        {
            result.EmptyDirectories.Add(new DirectoryEntry
            {
                RelativePath = ".",
                Operation = FileOperation.Added,
                Exists = true,
                LastModifiedUtc = Directory.GetLastWriteTimeUtc(root)
            });
        }

        return result;
    }

    public Task<IReadOnlyCollection<FileEntry>> ScanSourceAsync(string sourceDirectory, CancellationToken cancellationToken = default)
    {
        return ScanAsync(sourceDirectory, cancellationToken).ContinueWith(t => (IReadOnlyCollection<FileEntry>)t.Result.ConfirmedChanges, cancellationToken);
    }

    public async Task<IReadOnlyCollection<DirectoryEntry>> ScanDirectoriesAsync(string sourceDirectory, CancellationToken cancellationToken = default)
    {
        var result = await ScanAsync(sourceDirectory, cancellationToken).ConfigureAwait(false);
        return result.EmptyDirectories;
    }

    public async Task UpdateStateAsync(IEnumerable<FileEntry> archivedFiles, DateTimeOffset? lastFullVerificationUtc = null, CancellationToken cancellationToken = default)
    {
        if (archivedFiles is null)
        {
            return;
        }

        await _stateIndex.UpdateStateAsync(archivedFiles, lastFullVerificationUtc, cancellationToken).ConfigureAwait(false);
    }

    private static bool RequiresFullVerification(DateTimeOffset lastFullVerificationUtc, DateTimeOffset now, TimeSpan interval)
    {
        if (lastFullVerificationUtc == default)
        {
            return true;
        }

        return now - lastFullVerificationUtc >= interval;
    }

    private static bool TryReadFile(string path, out FileInfo fileInfo)
    {
        try
        {
            fileInfo = new FileInfo(path);
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);
            return stream.CanRead;
        }
        catch
        {
            fileInfo = new FileInfo(path);
            return false;
        }
    }
    
    private static async Task<bool> WaitForFileStabilityAsync(string filePath, CancellationToken cancellationToken)
    {
        var first = ComputeSha256(filePath);
        if (first is null) return false;

        for (var attempt = 0; attempt < MaxStabilityChecks; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(StabilityWaitMilliseconds, cancellationToken).ConfigureAwait(false);

            var second = ComputeSha256(filePath);
            if (second is null) return false;

            if (first == second) return true;
            first = second;
        }

        return false;
    }
    
    private static (long Length, DateTimeOffset LastWriteTimeUtc)? GetFileSnapshot(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return (info.Length, info.LastWriteTimeUtc);
        }
        catch
        {
            return null;
        }
    }

    private bool IsExcludedPath(string path, string root)
    {
        var candidate = Path.GetFullPath(path);
        if (string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var excludedPath in _excludedPaths)
        {
            if (string.Equals(candidate, excludedPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (candidate.StartsWith(excludedPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static DateTimeOffset NormalizeUtcTimestamp(DateTimeOffset value)
    {
        var utcTicks = value.UtcTicks;
        var millisecondTicks = TimeSpan.TicksPerMillisecond;
        var truncatedTicks = utcTicks - (utcTicks % millisecondTicks);
        return new DateTimeOffset(truncatedTicks, TimeSpan.Zero);
    }

    private static string GetRelativePath(string root, string path)
    {
        var relativePath = Path.GetRelativePath(root, path);
        return relativePath.Replace('\\', '/');
    }

    private static FileEntry CreateFileEntry(string relativePath, FileInfo fileInfo, string? sha256 = null)
    {
        return new FileEntry
        {
            RelativePath = relativePath,
            Operation = FileOperation.Added,
            FileSize = fileInfo.Length,
            LastModifiedUtc = fileInfo.LastWriteTimeUtc,
            Sha256 = sha256 ?? string.Empty
        };
    }

    private static string? ComputeSha256(string filePath)
    {
        try
        {
            using var sha256 = SHA256.Create();
            using var stream = new FileStream(
                filePath, 
                FileMode.Open, 
                FileAccess.Read, 
                FileShare.ReadWrite);
            var hash = sha256.ComputeHash(stream);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        catch
        {
            return null;
        }
    }
}
