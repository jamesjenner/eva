using EVA.Core.Interfaces;
using EVA.Core.Models;
using EVA.Infrastructure;
using Xunit;

namespace EVA.Tests;

public sealed class ArchiveRestorerTests : IDisposable
{
    private readonly List<string> _directories = [];

    public void Dispose()
    {
        foreach (var directory in _directories)
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RestoresSingleFullArchive()
    {
        var archiveDirectory = CreateDirectory();
        var destination = CreateDirectory();
        var archivePath = AddArchive(archiveDirectory, FullManifest("full", new[] { Added("one.txt") }));

        var result = await CreateRestorer(archiveDirectory).RestoreAsync(
            archivePath, destination, "password", RestoreMode.AlternativeLocation);

        Assert.True(result.Success);
        Assert.Contains(Path.Combine(destination, "one.txt"), result.FilesRestored);
        Assert.True(File.Exists(Path.Combine(destination, "one.txt")));
    }

    [Fact]
    public async Task RestoresIncrementalChainFromFullThroughSelectedArchive()
    {
        var archiveDirectory = CreateDirectory();
        var destination = CreateDirectory();
        var one = CreateContentFile("one-v1");
        var two = CreateContentFile("two");
        var oneV2 = CreateContentFile("one-v2");
        AddArchive(archiveDirectory, FullManifest("full", new[] { Added("one.txt", one, "one-v1") }));
        AddArchive(archiveDirectory, IncrementalManifest("incremental-1", "full", new[] { Added("two.txt", two, "two") }));
        var selected = AddArchive(archiveDirectory, IncrementalManifest("incremental-2", "incremental-1", new[] { Modified("one.txt", oneV2, "one-v2") }));

        var result = await CreateRestorer(archiveDirectory).RestoreAsync(
            selected, destination, "password", RestoreMode.AlternativeLocation);

        Assert.True(result.Success);
        Assert.Equal("one-v2", await File.ReadAllTextAsync(Path.Combine(destination, "one.txt")));
        Assert.Equal("two", await File.ReadAllTextAsync(Path.Combine(destination, "two.txt")));
        Assert.Equal(3, result.FilesRestored.Count);
    }

    [Fact]
    public async Task DeletedEntriesRemoveFilesAtDestination()
    {
        var archiveDirectory = CreateDirectory();
        var destination = CreateDirectory();
        var deletedPath = Path.Combine(destination, "gone.txt");
        await File.WriteAllTextAsync(deletedPath, "old");
        var archivePath = AddArchive(archiveDirectory, FullManifest("full", Array.Empty<FileEntry>(), new[] { Deleted("gone.txt") }));

        var result = await CreateRestorer(archiveDirectory).RestoreAsync(
            archivePath, destination, "password", RestoreMode.AlternativeLocation);

        Assert.True(result.Success);
        Assert.False(File.Exists(deletedPath));
    }

    [Fact]
    public async Task AlternativeLocationIsCreatedWhenMissing()
    {
        var archiveDirectory = CreateDirectory();
        var destination = Path.Combine(CreateDirectory(), "restore");
        var archivePath = AddArchive(archiveDirectory, FullManifest("full", new[] { Added("one.txt") }));

        var result = await CreateRestorer(archiveDirectory).RestoreAsync(
            archivePath, destination, "password", RestoreMode.AlternativeLocation);

        Assert.True(result.Success);
        Assert.True(Directory.Exists(destination));
    }

    [Fact]
    public async Task ExistingDestinationFilesAreReportedForOverwrite()
    {
        var archiveDirectory = CreateDirectory();
        var destination = CreateDirectory();
        await File.WriteAllTextAsync(Path.Combine(destination, "one.txt"), "existing");
        var archivePath = AddArchive(archiveDirectory, FullManifest("full", new[] { Added("one.txt") }));

        var result = await CreateRestorer(archiveDirectory).RestoreAsync(
            archivePath, destination, "password", RestoreMode.AlternativeLocation);

        Assert.True(result.Success);
        Assert.Contains(Path.Combine(destination, "one.txt"), result.FilesToOverwrite);
        Assert.Equal("existing", await File.ReadAllTextAsync(Path.Combine(destination, "one.txt")));
    }

    [Fact]
    public async Task IncrementalChainReplaysFinalStateAndExcludesLaterFiles()
    {
        var archiveDirectory = CreateDirectory();
        var destination = CreateDirectory();
        var one = CreateContentFile("one-v1");
        var two = CreateContentFile("two");
        var three = CreateContentFile("three");
        AddArchive(archiveDirectory, FullManifest("full", new[] { Added("one.txt", one, "one-v1") }));
        AddArchive(archiveDirectory, IncrementalManifest("incremental-1", "full", new[] { Added("two.txt", two, "two") }, new[] { Deleted("one.txt") }));
        var selected = AddArchive(archiveDirectory, IncrementalManifest("incremental-2", "incremental-1", Array.Empty<FileEntry>()));
        AddArchive(archiveDirectory, IncrementalManifest("incremental-3", "incremental-2", new[] { Added("three.txt", three, "three") }));

        var result = await CreateRestorer(archiveDirectory).RestoreAsync(
            selected, destination, "password", RestoreMode.AlternativeLocation);

        Assert.True(result.Success);
        Assert.False(File.Exists(Path.Combine(destination, "one.txt")));
        Assert.Equal("two", await File.ReadAllTextAsync(Path.Combine(destination, "two.txt")));
        Assert.False(File.Exists(Path.Combine(destination, "three.txt")));
    }

    [Fact]
    public async Task HashVerificationReportsCorruptedRestoredContent()
    {
        var archiveDirectory = CreateDirectory();
        var destination = CreateDirectory();
        var corruptedContent = CreateContentFile("corrupted");
        var archivePath = AddArchive(archiveDirectory, FullManifest("full", new[] { Added("one.txt", corruptedContent, "expected") }));

        var result = await CreateRestorer(archiveDirectory).RestoreAsync(
            archivePath, destination, "password", RestoreMode.AlternativeLocation);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("hash", StringComparison.OrdinalIgnoreCase));
    }

    private ArchiveRestorer CreateRestorer(string archiveDirectory)
    {
        return new ArchiveRestorer(new TestArchiveReader(_archives));
    }

    private readonly Dictionary<string, ArchiveManifest> _archives = new(StringComparer.OrdinalIgnoreCase);

    private string AddArchive(string directory, ArchiveManifest manifest)
    {
        var path = Path.Combine(directory, $"{manifest.CreatedUtc:yyyyMMddHHmmss}_{manifest.ArchiveId}.eva");
        File.WriteAllText(path, manifest.ArchiveId);
        _archives[path] = manifest;
        return path;
    }

    private string CreateDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        _directories.Add(path);
        return path;
    }

    private string CreateContentFile(string content)
    {
        var path = Path.Combine(CreateDirectory(), Guid.NewGuid().ToString("N") + ".content");
        File.WriteAllText(path, content);
        return path;
    }

    private static ArchiveManifest FullManifest(string id, IEnumerable<FileEntry> files, IEnumerable<DeletedEntry>? deleted = null) => new()
    {
        ArchiveId = id,
        ArchiveType = ArchiveType.Full,
        ChainId = "chain",
        CreatedUtc = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero),
        Files = files.ToList(),
        Deleted = deleted?.ToList() ?? []
    };

    private static ArchiveManifest IncrementalManifest(string id, string parentId, IEnumerable<FileEntry> files, IEnumerable<DeletedEntry>? deleted = null) => new()
    {
        ArchiveId = id,
        ArchiveType = ArchiveType.Incremental,
        ChainId = "chain",
        ParentArchiveId = parentId,
        CreatedUtc = new DateTimeOffset(2026, 9, 1, 12, 0, 1 + id.Length, TimeSpan.Zero),
        Files = files.ToList(),
        Deleted = deleted?.ToList() ?? []
    };

    private static FileEntry Added(string path, string? contentPath = null, string? expectedContent = null) => new()
    {
        RelativePath = path,
        Operation = FileOperation.Added,
        FileSize = expectedContent?.Length ?? 0,
        Sha256 = expectedContent is null ? string.Empty : ComputeSha256(expectedContent),
        ContentRef = contentPath is null ? null : new ContentRef { ObjectId = contentPath }
    };

    private static FileEntry Modified(string path, string? contentPath = null, string? expectedContent = null) => new()
    {
        RelativePath = path,
        Operation = FileOperation.Modified,
        FileSize = expectedContent?.Length ?? 0,
        Sha256 = expectedContent is null ? string.Empty : ComputeSha256(expectedContent),
        ContentRef = contentPath is null ? null : new ContentRef { ObjectId = contentPath }
    };
    private static DeletedEntry Deleted(string path) => new() { RelativePath = path };

    private static string ComputeSha256(string content)
    {
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexString(sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
    }

    private sealed class TestArchiveReader(Dictionary<string, ArchiveManifest> archives) : IArchiveReader
    {
        public Task<ArchiveHeader> ReadHeaderAsync(string archivePath, CancellationToken cancellationToken = default)
        {
            var manifest = archives[archivePath];
            return Task.FromResult(new ArchiveHeader
            {
                ArchiveId = manifest.ArchiveId,
                ArchiveType = manifest.ArchiveType,
                ChainId = manifest.ChainId,
                ParentArchiveId = manifest.ParentArchiveId,
                CreatedUtc = manifest.CreatedUtc
            });
        }

        public Task<ArchiveManifest> ReadManifestAsync(string archivePath, string password, CancellationToken cancellationToken = default)
            => Task.FromResult(archives[archivePath]);

        public Task<byte[]> ReadFileContentAsync(string archivePath, string relativePath, string password, CancellationToken cancellationToken = default)
        {
            var entry = archives[archivePath].Files.FirstOrDefault(file => string.Equals(file.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));
            if (entry?.ContentRef?.ObjectId is { } sourcePath && File.Exists(sourcePath))
            {
                return File.ReadAllBytesAsync(sourcePath, cancellationToken);
            }

            return Task.FromResult(Array.Empty<byte>());
        }

        public Task<bool> ValidateArchiveAsync(string archivePath, string password, CancellationToken cancellationToken = default)
            => Task.FromResult(true);
    }
}
