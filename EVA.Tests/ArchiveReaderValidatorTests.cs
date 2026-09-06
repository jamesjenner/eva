using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EVA.Core.Models;
using EVA.Infrastructure;
using Konscious.Security.Cryptography;
using Xunit;

namespace EVA.Tests;

public sealed class ArchiveReaderValidatorTests : IDisposable
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
    public async Task ValidArchiveWrittenByWriterCanBeReadBackCorrectly()
    {
        var tempDir = CreateTempDirectory();
        var archivePath = Path.Combine(tempDir, "archive.eva");
        var writer = new ArchiveWriter();
        var manifest = CreateManifest();

        await writer.WriteArchiveAsync(manifest, CreateFileEntries(), archivePath, "Password123!");

        var reader = new ArchiveReader();
        var result = await reader.ReadManifestAsync(archivePath, "Password123!");

        Assert.NotNull(result);
        Assert.Equal(manifest.ArchiveId, result.ArchiveId);
        Assert.Equal(manifest.ChainId, result.ChainId);
        Assert.Equal(manifest.SourceId, result.SourceId);
        Assert.Equal(manifest.CreatedUtc, result.CreatedUtc);
        Assert.Single(result.Files);
    }

    [Fact]
    public async Task ManifestFieldsMatchHeaderFieldsExactly()
    {
        var tempDir = CreateTempDirectory();
        var archivePath = Path.Combine(tempDir, "archive.eva");
        var writer = new ArchiveWriter();
        var manifest = CreateManifest();

        await writer.WriteArchiveAsync(manifest, CreateFileEntries(), archivePath, "Password123!");

        var bytes = await File.ReadAllBytesAsync(archivePath);
        var header = ParseHeader(bytes);
        var reader = new ArchiveReader();
        var result = await reader.ReadManifestAsync(archivePath, "Password123!");

        Assert.Equal(header.ArchiveId, result.ArchiveId);
        Assert.Equal(header.ChainId, result.ChainId);
        Assert.Equal(header.SourceId, result.SourceId);
        Assert.Equal(header.CreatedUtc, result.CreatedUtc);
        Assert.Equal(header.ParentArchiveId ?? string.Empty, result.ParentArchiveId ?? string.Empty);
        Assert.Equal(header.FormatVersion, result.FormatVersion);
    }

    [Fact]
    public async Task TruncatedFileIsRejectedBeforeDecryptionIsAttempted()
    {
        var tempDir = CreateTempDirectory();
        var archivePath = Path.Combine(tempDir, "archive.eva");
        var writer = new ArchiveWriter();

        await writer.WriteArchiveAsync(CreateManifest(), CreateFileEntries(), archivePath, "Password123!");

        var bytes = await File.ReadAllBytesAsync(archivePath);
        await File.WriteAllBytesAsync(archivePath, bytes[..^32]);

        var reader = new ArchiveReader();
        await Assert.ThrowsAnyAsync<Exception>(() => reader.ReadManifestAsync(archivePath, "Password123!"));
    }

    [Fact]
    public async Task WrongPasswordProducesAuthenticationFailureNotCorruptedData()
    {
        var tempDir = CreateTempDirectory();
        var archivePath = Path.Combine(tempDir, "archive.eva");
        var writer = new ArchiveWriter();

        await writer.WriteArchiveAsync(CreateManifest(), CreateFileEntries(), archivePath, "Password123!");

        var reader = new ArchiveReader();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadManifestAsync(archivePath, "WrongPassword!"));
        Assert.Contains("authentication", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TamperedHeaderByteIsDetectedAndRejected()
    {
        var tempDir = CreateTempDirectory();
        var archivePath = Path.Combine(tempDir, "archive.eva");
        var writer = new ArchiveWriter();

        await writer.WriteArchiveAsync(CreateManifest(), CreateFileEntries(), archivePath, "Password123!");

        var bytes = await File.ReadAllBytesAsync(archivePath);
        var archiveIdLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(20, 2));
        var tamperOffset = 46 + archiveIdLength;
        bytes[tamperOffset] ^= 0xFF;
        await File.WriteAllBytesAsync(archivePath, bytes);

        var reader = new ArchiveReader();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadManifestAsync(archivePath, "Password123!"));
        Assert.Contains("authentication", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TamperedPayloadByteIsDetectedAndRejected()
    {
        var tempDir = CreateTempDirectory();
        var archivePath = Path.Combine(tempDir, "archive.eva");
        var writer = new ArchiveWriter();

        await writer.WriteArchiveAsync(CreateManifest(), CreateFileEntries(), archivePath, "Password123!");

        var bytes = await File.ReadAllBytesAsync(archivePath);
        var headerLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4));
        var payloadStart = (int)headerLength;
        var payloadLength = bytes.Length - (int)headerLength - 16;
        bytes[payloadStart + payloadLength / 2] ^= 0x01;
        await File.WriteAllBytesAsync(archivePath, bytes);

        var reader = new ArchiveReader();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadManifestAsync(archivePath, "Password123!"));
        Assert.Contains("authentication", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidSingleSnapshotArchivePassesStructuralValidation()
    {
        var path = WriteArchiveForValidation();
        var validator = new ArchiveValidator();
        var archive = ParseHeader(File.ReadAllBytes(path));

        Assert.True(validator.ValidateHeader(archive));
    }

    [Fact]
    public void CompleteSnapshotPlusTwoIncrementalsPassesChainValidation()
    {
        var tempDir = CreateTempDirectory();
        var snapshot = CreateManifest("snapshot", null, "chain-1");
        var inc1 = CreateManifest("inc1", "snapshot", "chain-1");
        var inc2 = CreateManifest("inc2", "inc1", "chain-1");

        WriteArchive(tempDir, snapshot, "snapshot.eva");
        WriteArchive(tempDir, inc1, "inc1.eva");
        WriteArchive(tempDir, inc2, "inc2.eva");

        var validator = new ArchiveValidator();
        var headers = new[]
        {
            ParseHeader(File.ReadAllBytes(Path.Combine(tempDir, "snapshot.eva"))),
            ParseHeader(File.ReadAllBytes(Path.Combine(tempDir, "inc1.eva"))),
            ParseHeader(File.ReadAllBytes(Path.Combine(tempDir, "inc2.eva")))
        };

        var chain = new ArchiveChain
        {
            ChainId = "chain-1",
            ArchiveIds = new[] { "snapshot", "inc1", "inc2" }.ToList(),
            Status = ChainStatus.Complete
        };

        Assert.True(validator.ValidateArchiveChain(headers, chain));
    }

    [Fact]
    public void ChainWithMissingIntermediateArchiveIsIdentifiedAsIncomplete()
    {
        var tempDir = CreateTempDirectory();
        var snapshot = CreateManifest("snapshot", null, "chain-2");
        var inc2 = CreateManifest("inc2", "snapshot", "chain-2");

        WriteArchive(tempDir, snapshot, "snapshot.eva");
        WriteArchive(tempDir, inc2, "inc2.eva");

        var validator = new ArchiveValidator();
        var headers = new[]
        {
            ParseHeader(File.ReadAllBytes(Path.Combine(tempDir, "snapshot.eva"))),
            ParseHeader(File.ReadAllBytes(Path.Combine(tempDir, "inc2.eva")))
        };

        var chain = new ArchiveChain
        {
            ChainId = "chain-2",
            ArchiveIds = new[] { "snapshot", "inc2" }.ToList(),
            Status = ChainStatus.Incomplete,
            MissingArchiveId = "inc1"
        };

        Assert.False(validator.ValidateArchiveChain(headers, chain));
        Assert.Equal("inc1", chain.MissingArchiveId);
    }

    [Fact]
    public void ChainWhereParentArchiveIdDoesNotMatchReportsTheCorrectMissingArchiveId()
    {
        var tempDir = CreateTempDirectory();
        var snapshot = CreateManifest("snapshot", null, "chain-3");
        var broken = CreateManifest("broken", "missing-parent", "chain-3");

        WriteArchive(tempDir, snapshot, "snapshot.eva");
        WriteArchive(tempDir, broken, "broken.eva");

        var validator = new ArchiveValidator();
        var headers = new[]
        {
            ParseHeader(File.ReadAllBytes(Path.Combine(tempDir, "snapshot.eva"))),
            ParseHeader(File.ReadAllBytes(Path.Combine(tempDir, "broken.eva")))
        };

        var chain = new ArchiveChain
        {
            ChainId = "chain-3",
            ArchiveIds = new[] { "snapshot", "broken" }.ToList(),
            Status = ChainStatus.Missing,
            MissingArchiveId = "missing-parent"
        };

        Assert.False(validator.ValidateArchiveChain(headers, chain));
        Assert.Equal("missing-parent", chain.MissingArchiveId);
    }

    [Fact]
    public async Task ContentHashMismatchBetweenManifestAndDecryptedFileIsDetectedAndReported()
    {
        var tempDir = CreateTempDirectory();
        var archivePath = Path.Combine(tempDir, "archive.eva");
        var manifest = CreateManifest();
        var fileEntries = CreateFileEntries();

        var writer = new ArchiveWriter();
        await writer.WriteArchiveAsync(manifest, fileEntries, archivePath, "Password123!");

        var bytes = File.ReadAllBytes(archivePath);
        var headerLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4));
        var payloadStart = (int)headerLength;
        var payloadLength = bytes.Length - payloadStart - 16;
        bytes[payloadStart + payloadLength / 2] ^= 0xFF;
        await File.WriteAllBytesAsync(archivePath, bytes);

        var reader = new ArchiveReader();
        await Assert.ThrowsAnyAsync<Exception>(() => reader.ReadManifestAsync(archivePath, "Password123!"));
    }

    private string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(path);
        _tempDirectories.Add(path);
        return path;
    }

    private string WriteArchiveForValidation()
    {
        var dir = CreateTempDirectory();
        var path = Path.Combine(dir, "snapshot.eva");
        var writer = new ArchiveWriter();
        writer.WriteArchiveAsync(CreateManifest(), CreateFileEntries(), path, "Password123!").GetAwaiter().GetResult();
        return path;
    }

    private void WriteArchive(string dir, ArchiveManifest manifest, string fileName)
    {
        var path = Path.Combine(dir, fileName);
        var writer = new ArchiveWriter();
        writer.WriteArchiveAsync(manifest, CreateFileEntries(), path, "Password123!").GetAwaiter().GetResult();
    }

    private static ArchiveManifest CreateManifest(string? archiveId = null, string? parentArchiveId = null, string? chainId = null)
    {
        var id = archiveId ?? "archive-001";
        return new ArchiveManifest
        {
            FormatVersion = 1,
            ArchiveId = id,
            ArchiveType = ArchiveType.Snapshot,
            ChainId = chainId ?? "chain-001",
            ParentArchiveId = parentArchiveId,
            SourceId = "source-001",
            CreatedUtc = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero),
            Files = CreateFileEntries().ToList(),
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

    private static ArchiveHeader ParseHeader(byte[] archive)
    {
        var idLength = BinaryPrimitives.ReadUInt16LittleEndian(archive.AsSpan(20, 2));
        var chainLength = BinaryPrimitives.ReadUInt16LittleEndian(archive.AsSpan(22, 2));
        var parentLength = BinaryPrimitives.ReadUInt16LittleEndian(archive.AsSpan(24, 2));
        var sourceLength = BinaryPrimitives.ReadUInt16LittleEndian(archive.AsSpan(26, 2));
        var kdfIdLength = BinaryPrimitives.ReadUInt16LittleEndian(archive.AsSpan(36, 2));
        var kdfParametersLength = BinaryPrimitives.ReadUInt16LittleEndian(archive.AsSpan(38, 2));
        var saltLength = BinaryPrimitives.ReadUInt16LittleEndian(archive.AsSpan(40, 2));
        var algorithmLength = BinaryPrimitives.ReadUInt16LittleEndian(archive.AsSpan(42, 2));
        var nonceLength = BinaryPrimitives.ReadUInt16LittleEndian(archive.AsSpan(44, 2));

        var offset = 46;
        var archiveId = Encoding.UTF8.GetString(archive, offset, idLength); offset += idLength;
        var chainId = Encoding.UTF8.GetString(archive, offset, chainLength); offset += chainLength;
        var parentId = Encoding.UTF8.GetString(archive, offset, parentLength); offset += parentLength;
        var sourceId = Encoding.UTF8.GetString(archive, offset, sourceLength); offset += sourceLength;
        var kdfId = Encoding.UTF8.GetString(archive, offset, kdfIdLength); offset += kdfIdLength;

        var kdfParametersStart = offset;
        offset += kdfParametersLength;

        var saltStart = offset;
        offset += saltLength;

        var algorithm = Encoding.UTF8.GetString(archive, offset, algorithmLength); offset += algorithmLength;

        var nonceStart = offset;
        offset += nonceLength;

        return new ArchiveHeader
        {
            Magic = Encoding.ASCII.GetString(archive, 0, 4),
            FormatVersion = BinaryPrimitives.ReadUInt16LittleEndian(archive.AsSpan(4, 2)),
            ArchiveType = (ArchiveType)archive[6],
            ArchiveId = archiveId,
            ChainId = chainId,
            ParentArchiveId = string.IsNullOrEmpty(parentId) ? null : parentId,
            SourceId = sourceId,
            CreatedUtc = DateTimeOffset.FromUnixTimeMilliseconds(BinaryPrimitives.ReadInt64LittleEndian(archive.AsSpan(28, 8))),
            KdfIdentifier = kdfId,
            KdfParameters = archive.Skip(kdfParametersStart).Take(kdfParametersLength).ToArray(),
            Salt = archive.Skip(saltStart).Take(saltLength).ToArray(),
            EncryptionAlgorithmIdentifier = algorithm,
            Nonce = archive.Skip(nonceStart).Take(nonceLength).ToArray(),
            HeaderLength = BinaryPrimitives.ReadUInt32LittleEndian(archive.AsSpan(8, 4)),
            PayloadLength = (long)BinaryPrimitives.ReadUInt64LittleEndian(archive.AsSpan(12, 8))
        };
    }
}
