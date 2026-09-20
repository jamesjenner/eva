using System.Security.Cryptography;
using System.Text;
using EVA.Core.Models;
using EVA.Infrastructure;
using Xunit;

namespace EVA.Tests;

public sealed class ArchivePayloadTests : IDisposable
{
    private const string Password = "Password123!";
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
    public async Task WrittenArchiveContainsRestorableBytesForEveryNonDeletedEntry()
    {
        var source = CreateDirectory();
        var archivePath = Path.Combine(CreateDirectory(), "full.eva");
        await File.WriteAllTextAsync(Path.Combine(source, "one.txt"), "one");
        await File.WriteAllTextAsync(Path.Combine(source, "two.txt"), "two");

        var writer = new ArchiveWriter(source);
        await writer.WriteArchiveAsync(CreateManifest(),
            new[] { Entry("one.txt", "one"), Entry("two.txt", "two") }, archivePath, Password);

        var reader = new ArchiveReader();
        Assert.Equal("one", Encoding.UTF8.GetString(await reader.ReadFileContentAsync(archivePath, "one.txt", Password)));
        Assert.Equal("two", Encoding.UTF8.GetString(await reader.ReadFileContentAsync(archivePath, "two.txt", Password)));
    }

    [Fact]
    public async Task ReadingFileEntryReturnsOriginalBytesByteForByte()
    {
        var source = CreateDirectory();
        var archivePath = Path.Combine(CreateDirectory(), "full.eva");
        var bytes = new byte[] { 0, 1, 2, 127, 128, 255 };
        await File.WriteAllBytesAsync(Path.Combine(source, "binary.dat"), bytes);

        await new ArchiveWriter(source).WriteArchiveAsync(CreateManifest(), new[] { Entry("binary.dat", bytes) }, archivePath, Password);

        var actual = await new ArchiveReader().ReadFileContentAsync(archivePath, "binary.dat", Password);
        Assert.Equal(bytes, actual);
    }

    [Fact]
    public async Task DeletedEntriesHaveZeroContentBytes()
    {
        var source = CreateDirectory();
        var archivePath = Path.Combine(CreateDirectory(), "full.eva");
        await new ArchiveWriter(source).WriteArchiveAsync(CreateManifest(), new[] { Deleted("gone.txt") }, archivePath, Password);

        var content = await new ArchiveReader().ReadFileContentAsync(archivePath, "gone.txt", Password);
        Assert.Empty(content);
    }

    [Fact]
    public async Task EndOfPayloadMarkerMismatchThrowsOnRead()
    {
        var source = CreateDirectory();
        var archivePath = Path.Combine(CreateDirectory(), "full.eva");
        await File.WriteAllTextAsync(Path.Combine(source, "one.txt"), "one");
        await new ArchiveWriter(source).WriteArchiveAsync(CreateManifest(), new[] { Entry("one.txt", "one") }, archivePath, Password);

        var bytes = await File.ReadAllBytesAsync(archivePath);
        bytes[^17] ^= 0x01;
        await File.WriteAllBytesAsync(archivePath, bytes);

        await Assert.ThrowsAnyAsync<Exception>(() => new ArchiveReader().ReadManifestAsync(archivePath, Password));
    }

    [Fact]
    public async Task RoundTripPreservesAllFileContents()
    {
        var source = CreateDirectory();
        var archivePath = Path.Combine(CreateDirectory(), "full.eva");
        await File.WriteAllTextAsync(Path.Combine(source, "one.txt"), "one");
        await File.WriteAllTextAsync(Path.Combine(source, "two.txt"), "two");
        var entries = new[] { Entry("one.txt", "one"), Entry("two.txt", "two") };

        await new ArchiveWriter(source).WriteArchiveAsync(CreateManifest(), entries, archivePath, Password);
        var reader = new ArchiveReader();
        Assert.Equal("one", Encoding.UTF8.GetString(await reader.ReadFileContentAsync(archivePath, "one.txt", Password)));
        Assert.Equal("two", Encoding.UTF8.GetString(await reader.ReadFileContentAsync(archivePath, "two.txt", Password)));
    }

    [Fact]
    public async Task FullRestoreProducesCorrectContentAndHash()
    {
        var source = CreateDirectory();
        var archivePath = Path.Combine(CreateDirectory(), "full.eva");
        var destination = Path.Combine(CreateDirectory(), "restore");
        await File.WriteAllTextAsync(Path.Combine(source, "one.txt"), "one");
        await new ArchiveWriter(source).WriteArchiveAsync(CreateManifest(), new[] { Entry("one.txt", "one") }, archivePath, Password);

        var result = await new ArchiveRestorer().RestoreAsync(archivePath, destination, Password, RestoreMode.AlternativeLocation);

        Assert.True(result.Success);
        var restored = await File.ReadAllTextAsync(Path.Combine(destination, "one.txt"));
        Assert.Equal("one", restored);
        Assert.Equal(Hash("one"), Hash(restored));
    }

    [Fact]
    public async Task CorruptedArchiveFileContentFailsHashVerification()
    {
        var source = CreateDirectory();
        var archivePath = Path.Combine(CreateDirectory(), "full.eva");
        var destination = Path.Combine(CreateDirectory(), "restore");
        await File.WriteAllTextAsync(Path.Combine(source, "one.txt"), "one");
        await new ArchiveWriter(source).WriteArchiveAsync(CreateManifest(), new[] { Entry("one.txt", "different") }, archivePath, Password);

        var result = await new ArchiveRestorer().RestoreAsync(archivePath, destination, Password, RestoreMode.AlternativeLocation);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("hash", StringComparison.OrdinalIgnoreCase));
    }

    private string CreateDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        _directories.Add(path);
        return path;
    }

    private static ArchiveManifest CreateManifest() => new()
    {
        FormatVersion = 1,
        ArchiveId = Guid.NewGuid().ToString("N"),
        ArchiveType = ArchiveType.Full,
        ChainId = "chain",
        SourceId = "source",
        CreatedUtc = DateTimeOffset.UtcNow
    };

    private static FileEntry Entry(string path, string content) => Entry(path, Encoding.UTF8.GetBytes(content));

    private static FileEntry Entry(string path, byte[] content) => new()
    {
        RelativePath = path,
        Operation = FileOperation.Added,
        FileSize = content.Length,
        Sha256 = Hash(content)
    };

    private static FileEntry Deleted(string path) => new() { RelativePath = path, Operation = FileOperation.Deleted };

    private static string Hash(string content) => Hash(Encoding.UTF8.GetBytes(content));

    private static string Hash(byte[] content)
    {
        return Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
    }
}
