using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EVA.Core.Interfaces;
using EVA.Core.Models;

namespace EVA.Infrastructure;

public sealed class ScanStateSnapshot
{
    public Dictionary<string, FileStateEntry> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class FileStateEntry
{
    public string RelativePath { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public DateTimeOffset LastModifiedUtc { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public DateTimeOffset LastFullVerificationUtc { get; set; }
}

public sealed class LocalScanStateIndex : IArchiveIndex
{
    private readonly string _sourceDirectory;
    private readonly string _stateFilePath;

    public LocalScanStateIndex(string sourceDirectory, string? stateDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(sourceDirectory))
        {
            throw new ArgumentException("Source directory is required.", nameof(sourceDirectory));
        }

        _sourceDirectory = Path.GetFullPath(sourceDirectory);
        var appDataRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var stateRoot = stateDirectory ?? Path.Combine(appDataRoot, "EVA", "state");
        Directory.CreateDirectory(stateRoot);

        var sourceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_sourceDirectory))).ToLowerInvariant();
        _stateFilePath = Path.Combine(stateRoot, $"{sourceHash}.json");
    }

    public async Task<ScanStateSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_stateFilePath))
        {
            return new ScanStateSnapshot();
        }

        await using var stream = File.OpenRead(_stateFilePath);
        var snapshot = await JsonSerializer.DeserializeAsync<ScanStateSnapshot>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return snapshot ?? new ScanStateSnapshot();
    }

    public async Task SaveAsync(ScanStateSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(_stateFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(_stateFilePath);
        await JsonSerializer.SerializeAsync(stream, snapshot, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateStateAsync(IEnumerable<FileEntry> archivedFiles, DateTimeOffset? lastFullVerificationUtc = null, CancellationToken cancellationToken = default)
    {
        var snapshot = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var effectiveVerificationUtc = lastFullVerificationUtc ?? DateTimeOffset.UtcNow;

        foreach (var file in archivedFiles)
        {
            if (string.IsNullOrWhiteSpace(file.RelativePath))
            {
                continue;
            }

            snapshot.Files[file.RelativePath] = new FileStateEntry
            {
                RelativePath = file.RelativePath,
                FileSize = file.FileSize,
                LastModifiedUtc = file.LastModifiedUtc,
                Sha256 = file.Sha256,
                LastFullVerificationUtc = effectiveVerificationUtc
            };
        }

        await SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
    }

    Task IArchiveIndex.AddOrUpdateArchiveAsync(ArchiveHeader header, string archivePath, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    Task<IReadOnlyCollection<ArchiveHeader>> IArchiveIndex.GetArchivesAsync(string archiveDirectory, CancellationToken cancellationToken)
    {
        return Task.FromResult<IReadOnlyCollection<ArchiveHeader>>(Array.Empty<ArchiveHeader>());
    }

    Task<ArchiveChain> IArchiveIndex.GetChainAsync(string chainId, CancellationToken cancellationToken)
    {
        return Task.FromResult(new ArchiveChain
        {
            ChainId = chainId,
            ArchiveIds = new List<string>(),
            Status = ChainStatus.Complete
        });
    }

    Task IArchiveIndex.RemoveArchiveAsync(string archiveId, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
