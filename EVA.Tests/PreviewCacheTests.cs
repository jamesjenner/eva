using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using EVA.Core;
using EVA.Core.Interfaces;
using EVA.Core.Models;
using EVA.Infrastructure;
using Xunit;

namespace EVA.Tests;

public sealed class PreviewCacheTests : IDisposable
{
    private const string Password = "cache-test-password";
    private readonly string _tempRoot = CreateTempDirectory();
    private readonly string _cacheRoot;
    private readonly FakePasswordStore _passwordStore = new();

    public PreviewCacheTests()
    {
        _cacheRoot = Path.Combine(_tempRoot, "injected-cache-root");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ExtractsArchiveOnceCachesFilesSkipsDeletedAndDeletesArchiveCache()
    {
        var contentPath = await CreateContentFileAsync("cached payload");
        var emptyContentPath = Path.Combine(_tempRoot, "empty-payload.bin");
        await File.WriteAllBytesAsync(emptyContentPath, []);
        var archivePath = await WriteArchiveAsync(
            "cache-archive",
            [
                Entry("docs/readme.txt", contentPath, "cached payload"u8.ToArray()),
                Entry("empty.txt", emptyContentPath, []),
                Deleted("docs/gone.txt")
            ]);
        var cache = new PreviewCacheService(_cacheRoot);

        var first = await cache.EnsureCachedAsync(archivePath, _passwordStore.GetPassword()!);
        var second = await cache.EnsureCachedAsync(archivePath, _passwordStore.GetPassword()!);

        Assert.Equal(Path.Combine(_cacheRoot, "cache-archive"), first.CacheDirectory);
        Assert.Equal("cached payload", await File.ReadAllTextAsync(Path.Combine(first.CacheDirectory!, "docs", "readme.txt")));
        Assert.True(File.Exists(Path.Combine(first.CacheDirectory!, "empty.txt")));
        Assert.Empty(await File.ReadAllBytesAsync(Path.Combine(first.CacheDirectory!, "empty.txt")));
        Assert.False(File.Exists(Path.Combine(first.CacheDirectory!, "docs", "gone.txt")));
        Assert.Empty(first.SkippedEntries);
        Assert.True(second.WasAlreadyCached);

        await cache.DeleteArchiveAsync(first.ArchiveId);
        Assert.False(Directory.Exists(first.CacheDirectory));
    }

    [Theory]
    [InlineData("ciphertext")]
    [InlineData("aad")]
    [InlineData("truncated")]
    public async Task AuthenticationOrTruncationFailureLeavesNoCacheFiles(string tamperMode)
    {
        var contentPath = await CreateContentFileAsync("must not be extracted");
        var archivePath = await WriteArchiveAsync(
            $"tampered-{tamperMode}",
            [Entry("secret.txt", contentPath, "must not be extracted"u8.ToArray())]);
        await TamperArchiveAsync(archivePath, tamperMode);
        var cache = new PreviewCacheService(_cacheRoot);

        await Assert.ThrowsAnyAsync<Exception>(() => cache.EnsureCachedAsync(archivePath, _passwordStore.GetPassword()!));

        AssertCacheRootEmpty();
    }

    [Theory]
    [InlineData("../escaped.txt")]
    [InlineData("nested/../../escaped.txt")]
    [InlineData("C:\\outside.txt")]
    public async Task RejectsUnsafeArchivePathsAndRemovesStagingFolder(string relativePath)
    {
        var contentPath = await CreateContentFileAsync("unsafe");
        var archivePath = await WriteArchiveAsync(
            "unsafe-archive",
            [Entry(relativePath, contentPath, "unsafe"u8.ToArray())]);
        var cache = new PreviewCacheService(_cacheRoot);

        await Assert.ThrowsAnyAsync<InvalidDataException>(
            () => cache.EnsureCachedAsync(archivePath, _passwordStore.GetPassword()!));

        AssertCacheRootEmpty();
        Assert.False(File.Exists(Path.Combine(_tempRoot, "escaped.txt")));
    }

    [Fact]
    public async Task SkipsWindowsReservedAndTrailingDotNamesWithNotice()
    {
        var contentPath = await CreateContentFileAsync("invalid name contents");
        var archivePath = await WriteArchiveAsync(
            "invalid-names",
            [
                Entry("CON.txt", contentPath, "invalid name contents"u8.ToArray()),
                Entry("trailing.", contentPath, "invalid name contents"u8.ToArray()),
                Entry("valid.txt", contentPath, "invalid name contents"u8.ToArray())
            ]);
        var cache = new PreviewCacheService(_cacheRoot);

        var result = await cache.EnsureCachedAsync(archivePath, _passwordStore.GetPassword()!);

        Assert.Equal(2, result.SkippedEntries.Count);
        Assert.False(File.Exists(Path.Combine(result.CacheDirectory!, "CON.txt")));
        Assert.False(File.Exists(Path.Combine(result.CacheDirectory!, "trailing.")));
        Assert.True(File.Exists(Path.Combine(result.CacheDirectory!, "valid.txt")));
    }

    [Fact]
    public async Task CancellationDuringExtractionLeavesNoStagingOrPartialCacheFolder()
    {
        var contentPath = await CreateContentFileAsync("cancelled");
        var archivePath = await WriteArchiveAsync(
            "cancelled-archive",
            [Entry("file.txt", contentPath, "cancelled"u8.ToArray())]);
        var extractor = new BlockingExtractor();
        var cache = new PreviewCacheService(_cacheRoot, payloadExtractor: extractor);
        using var cancellation = new CancellationTokenSource();

        var extraction = cache.EnsureCachedAsync(archivePath, _passwordStore.GetPassword()!, cancellation.Token);
        await extractor.Started.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => extraction);

        AssertCacheRootEmpty();
    }

    [Fact]
    public async Task DeleteAllRemovesInjectedCacheRoot()
    {
        var contentPath = await CreateContentFileAsync("cache cleanup");
        var archivePath = await WriteArchiveAsync(
            "delete-all-archive",
            [Entry("file.txt", contentPath, "cache cleanup"u8.ToArray())]);
        var cache = new PreviewCacheService(_cacheRoot);
        _ = await cache.EnsureCachedAsync(archivePath, _passwordStore.GetPassword()!);

        cache.DeleteAll();

        Assert.False(Directory.Exists(_cacheRoot));
    }

    [Fact]
    public async Task TextPreviewReadsOnlyTheCappedBytesFromCachedFile()
    {
        var content = Encoding.UTF8.GetBytes(new string('x', ArchiveContentProjection.MaxPreviewBytes + 8192));
        var path = Path.Combine(_tempRoot, "large-preview.txt");
        await File.WriteAllBytesAsync(path, content);

        var preview = await ArchiveContentProjection.CreateTextPreviewAsync(path);

        Assert.True(preview.IsSupported);
        Assert.Equal(ArchiveContentProjection.MaxPreviewBytes, Encoding.UTF8.GetByteCount(preview.Text));
        Assert.True(preview.IsTruncated);
    }

    [Fact]
    public async Task ArchiveSizeGuardReturnsTooLargeWithoutCreatingCache()
    {
        var archivePath = Path.Combine(_tempRoot, "too-large.eva");
        await using (var stream = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write))
        {
            stream.SetLength(PreviewCacheService.MaxArchiveSizeBytes + 1);
        }

        var cache = new PreviewCacheService(_cacheRoot);
        var result = await cache.EnsureCachedAsync(archivePath, _passwordStore.GetPassword()!);

        Assert.True(result.IsTooLarge);
        AssertCacheRootEmpty();
    }

    private async Task<string> CreateContentFileAsync(string content)
    {
        var path = Path.Combine(_tempRoot, $"payload-{Guid.NewGuid():N}.bin");
        await File.WriteAllTextAsync(path, content, Encoding.UTF8);
        return path;
    }

    private async Task<string> WriteArchiveAsync(string archiveId, IReadOnlyCollection<FileEntry> entries)
    {
        var archivePath = Path.Combine(_tempRoot, $"{archiveId}.eva");
        var manifest = new ArchiveManifest
        {
            FormatVersion = 1,
            ArchiveId = archiveId,
            ChainId = "test-chain",
            SourceId = "test-source",
            CreatedUtc = DateTimeOffset.UtcNow,
            ArchiveType = ArchiveType.Full
        };

        await new ArchiveWriter().WriteArchiveAsync(manifest, entries, archivePath, _passwordStore.GetPassword()!);
        return archivePath;
    }

    private static FileEntry Entry(string relativePath, string contentPath, byte[] bytes) => new()
    {
        RelativePath = relativePath,
        Operation = FileOperation.Added,
        FileSize = bytes.Length,
        LastModifiedUtc = DateTimeOffset.UtcNow,
        Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
        ContentRef = new ContentRef
        {
            Kind = "bytes",
            ObjectId = contentPath,
            Length = bytes.Length,
            Compression = "none"
        }
    };

    private static FileEntry Deleted(string relativePath) => new()
    {
        RelativePath = relativePath,
        Operation = FileOperation.Deleted,
        FileSize = 0,
        LastModifiedUtc = DateTimeOffset.UtcNow
    };

    private static async Task TamperArchiveAsync(string archivePath, string mode)
    {
        var bytes = await File.ReadAllBytesAsync(archivePath);
        switch (mode)
        {
            case "ciphertext":
                var headerLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4));
                bytes[headerLength] ^= 1;
                break;
            case "aad":
                var archiveIdLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(20, 2));
                bytes[46 + archiveIdLength] ^= 1;
                break;
            case "truncated":
                bytes = bytes[..^1];
                break;
        }

        await File.WriteAllBytesAsync(archivePath, bytes);
    }

    private void AssertCacheRootEmpty()
    {
        Assert.True(!Directory.Exists(_cacheRoot) || !Directory.EnumerateFileSystemEntries(_cacheRoot).Any());
    }

    private sealed class BlockingExtractor : IArchivePayloadExtractor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ArchiveExtractionResult> ExtractFilesAsync(
            string archivePath,
            string password,
            string destinationDirectory,
            CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(destinationDirectory);
            await File.WriteAllTextAsync(Path.Combine(destinationDirectory, "partial.txt"), "partial", cancellationToken);
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new ArchiveExtractionResult("never", Array.Empty<string>());
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"eva-preview-cache-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}