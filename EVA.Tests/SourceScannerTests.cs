using EVA.Core.Models;
using EVA.Infrastructure;
using Xunit;

namespace EVA.Tests;

public sealed class SourceScannerTests : IDisposable
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
    public async Task NewFileInSourceDirectoryIsDetectedAsAdded()
    {
        var root = CreateTempDirectory();
        var filePath = Path.Combine(root, "new.txt");
        await File.WriteAllTextAsync(filePath, "alpha");

        var scanner = new SourceScanner(root, new LocalScanStateIndex(root, CreateTempDirectory()));
        var result = await scanner.ScanAsync(root, CancellationToken.None);

        Assert.Single(result.ConfirmedChanges);
        Assert.Equal("new.txt", result.ConfirmedChanges[0].RelativePath);
        Assert.Equal(FileOperation.Added, result.ConfirmedChanges[0].Operation);
    }

    [Fact]
    public async Task ModifiedFileIsDetectedAfterContentChange()
    {
        var root = CreateTempDirectory();
        var filePath = Path.Combine(root, "data.txt");
        await File.WriteAllTextAsync(filePath, "before");

        var state = new LocalScanStateIndex(root, CreateTempDirectory());
        var scanner = new SourceScanner(root, state);
        await scanner.UpdateStateAsync(new[]
        {
            new FileEntry
            {
                RelativePath = "data.txt",
                FileSize = 6,
                LastModifiedUtc = File.GetLastWriteTimeUtc(filePath),
                Sha256 = ComputeSha256("before")
            }
        });

        await File.WriteAllTextAsync(filePath, "after");

        var result = await scanner.ScanAsync(root, CancellationToken.None);
        Assert.Single(result.ConfirmedChanges);
        Assert.Equal("data.txt", result.ConfirmedChanges[0].RelativePath);
        Assert.Equal(FileOperation.Modified, result.ConfirmedChanges[0].Operation);
    }

    [Fact]
    public async Task DeletedFileIsDetectedAsDeleted()
    {
        var root = CreateTempDirectory();
        var filePath = Path.Combine(root, "gone.txt");
        await File.WriteAllTextAsync(filePath, "gone");

        var state = new LocalScanStateIndex(root, CreateTempDirectory());
        var scanner = new SourceScanner(root, state);
        await scanner.UpdateStateAsync(new[]
        {
            new FileEntry
            {
                RelativePath = "gone.txt",
                FileSize = 4,
                LastModifiedUtc = File.GetLastWriteTimeUtc(filePath),
                Sha256 = ComputeSha256("gone")
            }
        });

        File.Delete(filePath);

        var result = await scanner.ScanAsync(root, CancellationToken.None);
        Assert.Single(result.ConfirmedChanges);
        Assert.Equal("gone.txt", result.ConfirmedChanges[0].RelativePath);
        Assert.Equal(FileOperation.Deleted, result.ConfirmedChanges[0].Operation);
    }

    [Fact]
    public async Task EmptyDirectoryIsRecordedInScanResult()
    {
        var root = CreateTempDirectory();
        Directory.CreateDirectory(Path.Combine(root, "empty"));

        var scanner = new SourceScanner(root, new LocalScanStateIndex(root, CreateTempDirectory()));
        var result = await scanner.ScanAsync(root, CancellationToken.None);

        Assert.Single(result.EmptyDirectories);
        Assert.Equal("empty", result.EmptyDirectories[0].RelativePath);
    }

    [Fact]
    public async Task UnstableFileIsNotIncludedInConfirmedChanges()
    {
        var root = CreateTempDirectory();
        var filePath = Path.Combine(root, "unstable.txt");

        await File.WriteAllTextAsync(filePath, "one");

        // start writing BEFORE the scanner starts
        var writeStarted = new TaskCompletionSource();
        var writerTask = Task.Run(async () =>
        {
            using var stream = File.Open(filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            writeStarted.SetResult(); // signal that writing has started
            var buffer = new byte[1024];
            for (var i = 0; i < 20; i++)
            {
                await stream.WriteAsync(buffer);
                await stream.FlushAsync();
                await Task.Delay(300);
            }
        });

        // wait until writing has actually started before scanning
        await writeStarted.Task;

        var scanner = new SourceScanner(root, new LocalScanStateIndex(root, CreateTempDirectory()), verificationInterval: TimeSpan.FromMinutes(1));
        var result = await scanner.ScanAsync(root, CancellationToken.None);
        await writerTask;

        Assert.Empty(result.ConfirmedChanges);
        Assert.Single(result.UnstableFiles);
        Assert.Equal("unstable.txt", result.UnstableFiles[0].RelativePath);
    }
    
    [Fact]
    public async Task UnstableFileIsReportedAsErrorAndNotMarkedAsSuccessfullyScanned()
    {
        var root = CreateTempDirectory();
        var filePath = Path.Combine(root, "locked.txt");
        await File.WriteAllTextAsync(filePath, "hello");

        var scanner = new SourceScanner(root, new LocalScanStateIndex(root, CreateTempDirectory()));
        using var handle = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.None);

        var result = await scanner.ScanAsync(root, CancellationToken.None);

        Assert.Empty(result.ConfirmedChanges);
        Assert.Single(result.UnstableFiles);
        Assert.Equal("locked.txt", result.UnstableFiles[0].RelativePath);
    }

    [Fact]
    public async Task FullSha256VerificationRunsWhenVerificationIntervalHasElapsed()
    {
        var root = CreateTempDirectory();
        var filePath = Path.Combine(root, "tracked.txt");
        await File.WriteAllTextAsync(filePath, "version-1");

        var state = new LocalScanStateIndex(root, CreateTempDirectory());
        var scanner = new SourceScanner(root, state, verificationInterval: TimeSpan.FromHours(1));
        await scanner.UpdateStateAsync(new[]
        {
            new FileEntry
            {
                RelativePath = "tracked.txt",
                FileSize = 10,
                LastModifiedUtc = File.GetLastWriteTimeUtc(filePath),
                Sha256 = ComputeSha256("version-1"),
                ContentRef = null
            }
        }, lastFullVerificationUtc: DateTimeOffset.UtcNow.AddHours(-2));

        var result = await scanner.ScanAsync(root, CancellationToken.None);

        Assert.Empty(result.ConfirmedChanges);
        Assert.Single(result.StableFiles);
    }

    [Fact]
    public async Task FullSha256VerificationDoesNotRunWhenIntervalHasNotElapsedAndMetadataIsUnchanged()
    {
        var root = CreateTempDirectory();
        var filePath = Path.Combine(root, "tracked.txt");
        await File.WriteAllTextAsync(filePath, "version-1");

        var state = new LocalScanStateIndex(root, CreateTempDirectory());
        var scanner = new SourceScanner(root, state, verificationInterval: TimeSpan.FromHours(24));
        await scanner.UpdateStateAsync(new[]
        {
            new FileEntry
            {
                RelativePath = "tracked.txt",
                FileSize = 10,
                LastModifiedUtc = File.GetLastWriteTimeUtc(filePath),
                Sha256 = ComputeSha256("version-1")
            }
        }, lastFullVerificationUtc: DateTimeOffset.UtcNow.AddHours(-1));

        var result = await scanner.ScanAsync(root, CancellationToken.None);

        Assert.Empty(result.ConfirmedChanges);
        Assert.Single(result.StableFiles);
        Assert.Equal("tracked.txt", result.StableFiles[0].RelativePath);
    }

    [Fact]
    public async Task StateIsPersistedAndReloadedCorrectlyAcrossScannerInstances()
    {
        var root = CreateTempDirectory();
        var filePath = Path.Combine(root, "persisted.txt");
        await File.WriteAllTextAsync(filePath, "persisted-data");

        var stateDirectory = CreateTempDirectory();
        var firstIndex = new LocalScanStateIndex(root, stateDirectory);
        var firstScanner = new SourceScanner(root, firstIndex);
        await firstScanner.UpdateStateAsync(new[]
        {
            new FileEntry
            {
                RelativePath = "persisted.txt",
                FileSize = 14,
                LastModifiedUtc = File.GetLastWriteTimeUtc(filePath),
                Sha256 = ComputeSha256("persisted-data")
            }
        });

        var secondIndex = new LocalScanStateIndex(root, stateDirectory);
        var snapshot = await secondIndex.LoadAsync(CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Single(snapshot.Files);
        Assert.Contains("persisted.txt", snapshot.Files.Keys);
    }

    [Fact]
    public async Task MissingStateFileDoesNotCauseFailureAndScannerRebuildsFromFilesystem()
    {
        var root = CreateTempDirectory();
        var filePath = Path.Combine(root, "new.txt");
        await File.WriteAllTextAsync(filePath, "alpha");

        var index = new LocalScanStateIndex(root, CreateTempDirectory());
        var scanner = new SourceScanner(root, index);
        var result = await scanner.ScanAsync(root, CancellationToken.None);

        Assert.Single(result.ConfirmedChanges);
        Assert.Equal("new.txt", result.ConfirmedChanges[0].RelativePath);
    }

    [Fact]
    public async Task StateIsUpdatedOnlyForFilesConfirmedAsSuccessfullyArchived()
    {
        var root = CreateTempDirectory();
        var filePath = Path.Combine(root, "archive-me.txt");
        await File.WriteAllTextAsync(filePath, "payload");

        var index = new LocalScanStateIndex(root, CreateTempDirectory());
        var scanner = new SourceScanner(root, index);

        var result = await scanner.ScanAsync(root, CancellationToken.None);
        await scanner.UpdateStateAsync(result.ConfirmedChanges);

        var snapshot = await index.LoadAsync(CancellationToken.None);
        Assert.Single(snapshot.Files);
        Assert.Equal("archive-me.txt", snapshot.Files.Keys.First());
    }

    private string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(path);
        _tempDirectories.Add(path);
        return path;
    }

    private static string ComputeSha256(string value)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        var hash = sha.ComputeHash(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
