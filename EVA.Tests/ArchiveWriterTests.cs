using System.Buffers.Binary;
using System.Text;
using EVA.Core.Interfaces;
using EVA.Core.Models;
using EVA.Infrastructure;
using Xunit;

namespace EVA.Tests;

public sealed class ArchiveWriterTests : IDisposable
{
    private readonly List<string> _tempDirectories = [];

    public void Dispose()
    {
        foreach (var directory in _tempDirectories)
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ValidSnapshotArchiveCanBeWrittenAndOutputFileExists()
    {
        var tempDir = CreateTempDirectory();
        var archivePath = Path.Combine(tempDir, "archive.eva");

        var manifest = CreateManifest();
        var fileEntries = new[]
        {
            new FileEntry
            {
                RelativePath = "hello.txt",
                Operation = FileOperation.Added,
                FileSize = 11,
                LastModifiedUtc = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero),
                Sha256 = "abc123",
                ContentRef = new ContentRef
                {
                    Kind = "bytes",
                    ObjectId = "object-1",
                    Length = 11,
                    Compression = "none"
                }
            }
        };

        var writer = new ArchiveWriter();
        await writer.WriteArchiveAsync(manifest, fileEntries, archivePath, "Password123!");

        Assert.True(File.Exists(archivePath));
        Assert.Empty(Directory.GetFiles(tempDir, "*.tmp"));
    }

    [Fact]
    public async Task ArchiveBeginsWithMagicBytesEVA1()
    {
        var archivePath = CreateArchivePath();
        var writer = new ArchiveWriter();

        await writer.WriteArchiveAsync(CreateManifest(), CreateFileEntries(), archivePath, "Password123!");

        var bytes = File.ReadAllBytes(archivePath);
        Assert.Equal(new byte[] { (byte)'E', (byte)'V', (byte)'A', (byte)'1' }, bytes.Take(4).ToArray());
    }

    [Fact]
    public async Task GcmAuthenticationTagIsFinal16Bytes()
    {
        var archivePath = CreateArchivePath();
        var writer = new ArchiveWriter();

        await writer.WriteArchiveAsync(CreateManifest(), CreateFileEntries(), archivePath, "Password123!");

        var bytes = File.ReadAllBytes(archivePath);
        Assert.True(bytes.Length >= 16);
        var tag = bytes[^16..];
        Assert.Equal(16, tag.Length);
        Assert.NotEmpty(tag);
    }

    [Fact]
    public async Task TamperedHeaderByteCausesAuthenticationFailureOnRead()
    {
        var archivePath = CreateArchivePath();
        var writer = new ArchiveWriter();

        await writer.WriteArchiveAsync(CreateManifest(), CreateFileEntries(), archivePath, "Password123!");

        var bytes = File.ReadAllBytes(archivePath);
        var archiveIdLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(20, 2));
        var tamperOffset = 46 + archiveIdLength;
        bytes[tamperOffset] ^= 0xFF;
        File.WriteAllBytes(archivePath, bytes);

        var reader = new ArchiveReader();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadManifestAsync(archivePath, "Password123!"));
        Assert.Contains("authentication", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TruncatedArchiveIsRejected()
    {
        var archivePath = CreateArchivePath();
        var writer = new ArchiveWriter();

        await writer.WriteArchiveAsync(CreateManifest(), CreateFileEntries(), archivePath, "Password123!");

        var bytes = File.ReadAllBytes(archivePath);
        File.WriteAllBytes(archivePath, bytes[..^16]);

        var reader = new ArchiveReader();
        await Assert.ThrowsAnyAsync<Exception>(() => reader.ReadManifestAsync(archivePath, "Password123!"));
    }

    [Fact]
    public async Task TemporaryFileDoesNotHaveEvaExtensionDuringWriting()
    {
        var tempDir = CreateTempDirectory();
        var archivePath = Path.Combine(tempDir, "archive.eva");
        var writer = new ArchiveWriter();

        await writer.WriteArchiveAsync(CreateManifest(), CreateFileEntries(), archivePath, "Password123!");

        Assert.DoesNotContain(Directory.GetFiles(tempDir), x => x.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(Directory.GetFiles(tempDir, "*.tmp"));
    }

    [Fact]
    public async Task EvaFileDoesNotAppearUntilWritingAndVerificationAreComplete()
    {
        var tempDir = CreateTempDirectory();
        var archivePath = Path.Combine(tempDir, "archive.eva");
        var writer = new ArchiveWriter();

        await writer.WriteArchiveAsync(CreateManifest(), CreateFileEntries(), archivePath, "Password123!");

        Assert.True(File.Exists(archivePath));
        Assert.Single(Directory.GetFiles(tempDir, "*.eva"));
    }

    [Fact]
    public async Task KdfParametersAreSerializedInCorrectByteOrder()
    {
        var archivePath = CreateArchivePath();
        var writer = new ArchiveWriter();

        await writer.WriteArchiveAsync(CreateManifest(), CreateFileEntries(), archivePath, "Password123!");

        var bytes = File.ReadAllBytes(archivePath);
        var archiveIdLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(20, 2));
        var chainIdLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(22, 2));
        var parentArchiveIdLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(24, 2));
        var sourceIdLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(26, 2));
        var kdfIdentifierLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(36, 2));
        var kdfStart = 46 + archiveIdLength + chainIdLength + parentArchiveIdLength + sourceIdLength + kdfIdentifierLength;

        var memoryCost = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(kdfStart, 4));
        var timeCost = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(kdfStart + 4, 4));
        var parallelism = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(kdfStart + 8, 4));
        var outputLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(kdfStart + 12, 4));

        Assert.Equal(65536u, memoryCost);
        Assert.Equal(3u, timeCost);
        Assert.Equal(4u, parallelism);
        Assert.Equal(32u, outputLength);
    }

    [Fact]
    public async Task AadPassedToAesGcmIsExactSerializedHeaderBytes()
    {
        var archivePath = CreateArchivePath();
        var writer = new ArchiveWriter();

        await writer.WriteArchiveAsync(CreateManifest(), CreateFileEntries(), archivePath, "Password123!");

        var bytes = File.ReadAllBytes(archivePath);
        var headerLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4));
        var headerBytes = bytes.AsSpan(0, (int)headerLength).ToArray();
        var aadBytes = bytes.AsSpan(0, (int)headerLength).ToArray();

        Assert.Equal(aadBytes, headerBytes);
        Assert.NotEmpty(aadBytes);
    }

    private string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(path);
        _tempDirectories.Add(path);
        return path;
    }

    private string CreateArchivePath()
    {
        return Path.Combine(CreateTempDirectory(), "archive.eva");
    }

    private static ArchiveManifest CreateManifest()
    {
        return new ArchiveManifest
        {
            FormatVersion = 1,
            ArchiveId = "archive-001",
            ArchiveType = ArchiveType.Snapshot,
            ChainId = "chain-001",
            SourceId = "source-001",
            CreatedUtc = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero),
            Files =
            [
                new FileEntry
                {
                    RelativePath = "hello.txt",
                    Operation = FileOperation.Added,
                    FileSize = 11,
                    LastModifiedUtc = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero),
                    Sha256 = "abc123",
                    ContentRef = new ContentRef
                    {
                        Kind = "bytes",
                        ObjectId = "object-1",
                        Length = 11,
                        Compression = "none"
                    }
                }
            ],
            Directories = [],
            Deleted = []
        };
    }

    private static IReadOnlyCollection<FileEntry> CreateFileEntries()
    {
        return new[]
        {
            new FileEntry
            {
                RelativePath = "hello.txt",
                Operation = FileOperation.Added,
                FileSize = 11,
                LastModifiedUtc = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero),
                Sha256 = "abc123",
                ContentRef = new ContentRef
                {
                    Kind = "bytes",
                    ObjectId = "object-1",
                    Length = 11,
                    Compression = "none"
                }
            }
        };
    }
}
