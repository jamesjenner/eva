using System.Security.Cryptography;
using System.Text;
using EVA.App;
using EVA.Core.Interfaces;
using EVA.Core.Models;
using EVA.Infrastructure;
using Xunit;

namespace EVA.Tests;

public sealed class FailureVisibilityTests : IDisposable
{
    private readonly string _root = CreateTempDirectory();
    private readonly FakePasswordStore _passwordStore = new();

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void FileLoggerRollsWithinInjectedSizeAndFileCountLimits()
    {
        var logDirectory = Path.Combine(_root, "logs");
        var logger = new FileEventLogger(logDirectory, maxFileSizeBytes: 512, retainedFileCount: 2);

        logger.LogInformation(new string('a', 700));
        logger.LogWarning(new string('b', 700));
        logger.LogError(new string('c', 700));

        var logs = Directory.GetFiles(logDirectory, "*.log*");
        Assert.InRange(logs.Length, 1, 2);
        Assert.All(logs, path => Assert.InRange(new FileInfo(path).Length, 1, 512));
    }

    [Fact]
    public async Task OrchestratorReturnsFailureReasonAndLogDoesNotContainPassword()
    {
        var sentinelPassword = "SENTINEL-PASSWORD-MUST-NOT-BE-LOGGED";
        _passwordStore.SavePassword(sentinelPassword);
        var (source, destination, scanner, stateIndex, _) = await CreateSeededSourceAsync("small payload");
        var logDirectory = Path.Combine(_root, "run-logs");
        var orchestrator = CreateOrchestrator(
            source,
            destination,
            scanner,
            stateIndex,
            new ThrowingArchiveWriter(new IOException($"Synthetic disk write failure {sentinelPassword}")),
            null,
            sentinelPassword,
            logDirectory: logDirectory);

        var result = await orchestrator.RunOnceAsync(
            new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero),
            manualSnapshot: true);

        Assert.False(result.Success);
        Assert.Contains("Synthetic disk write failure", result.Error);
        Assert.Equal(nameof(IOException), result.FailureType);
        var logContents = string.Join(Environment.NewLine,
            Directory.GetFiles(logDirectory).Select(File.ReadAllText));
        Assert.Contains("Synthetic disk write failure", logContents);
        Assert.DoesNotContain(sentinelPassword, logContents, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FullBackupPreflightRejectsOversizeBeforeCallingWriter()
    {
        var (source, destination, scanner, stateIndex, fileSize) = await CreateSeededSourceAsync("preflight bytes");
        var writer = new CountingArchiveWriter();
        var logger = new TestEventLogger();
        var orchestrator = CreateOrchestrator(
            source,
            destination,
            scanner,
            stateIndex,
            writer,
            logger,
            _passwordStore.GetPassword()!,
            maxFullBackupSourceBytes: fileSize - 1);

        var result = await orchestrator.RunOnceAsync(
            new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero),
            manualSnapshot: true);

        Assert.False(result.Success);
        Assert.Contains("source size exceeds", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, writer.CallCount);
        Assert.Contains(logger.Errors, message => message.Contains("source size exceeds", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FullBackupPreflightAllowsSourceAtInjectedLimit()
    {
        var (source, destination, scanner, stateIndex, fileSize) = await CreateSeededSourceAsync("allowed bytes");
        var writer = new CountingArchiveWriter();
        var orchestrator = CreateOrchestrator(
            source,
            destination,
            scanner,
            stateIndex,
            writer,
            new TestEventLogger(),
            _passwordStore.GetPassword()!,
            maxFullBackupSourceBytes: fileSize);

        var result = await orchestrator.RunOnceAsync(
            new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero),
            manualSnapshot: true);

        Assert.True(result.Success);
        Assert.Equal(1, writer.CallCount);
    }

    [Fact]
    public async Task IncrementalBackupIsNotBlockedByFullBackupSizeLimit()
    {
        var source = Path.Combine(_root, "incremental-source");
        var destination = Path.Combine(_root, "incremental-archives");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(source, "incremental.txt"), "more than one byte");
        var index = new LocalScanStateIndex(source, Path.Combine(_root, "incremental-state"));
        var logger = new TestEventLogger();
        var scanner = new SourceScanner(source, index, logger: logger, excludedPaths: [destination]);
        var writer = new CountingArchiveWriter();
        var orchestrator = CreateOrchestrator(
            source,
            destination,
            scanner,
            index,
            writer,
            logger,
            _passwordStore.GetPassword()!,
            maxFullBackupSourceBytes: 1);

        var result = await orchestrator.RunOnceAsync(
            new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero));

        Assert.True(result.Success);
        Assert.Equal(ArchiveType.Incremental, result.ArchiveType);
        Assert.Equal(1, writer.CallCount);
    }

    [Fact]
    public void BackupFailurePresentationPersistsFailureWithoutUnstableFilesAndClearsOnSuccess()
    {
        var presentation = new BackupFailurePresentation();
        presentation.Apply(new BackupRunResult
        {
            Success = false,
            Error = "synthetic",
            FailureType = nameof(IOException),
            UnstableFiles = Array.Empty<FileEntry>()
        });

        Assert.Equal("A file or disk error stopped the backup. See the log.", presentation.Message);

        presentation.Apply(new BackupRunResult { Success = true });

        Assert.Null(presentation.Message);
    }

    [Fact]
    public void BackupFailurePresentationMapsOutOfMemoryAndUnknownErrors()
    {
        var presentation = new BackupFailurePresentation();
        presentation.Apply(new BackupRunResult
        {
            Success = false,
            FailureType = nameof(OutOfMemoryException)
        });
        Assert.Equal("Not enough memory to create the backup. See the log.", presentation.Message);

        presentation.Apply(new BackupRunResult { Success = false, FailureType = "UnexpectedFailure" });
        Assert.Equal("The backup failed. See the log.", presentation.Message);
    }

    [Fact]
    public async Task ArchiveLoadSizeGuardStopsBeforeManifestReaderIsCalled()
    {
        var path = Path.Combine(_root, "too-large.eva");
        await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        {
            stream.SetLength(PreviewCacheService.MaxArchiveSizeBytes + 1);
        }

        var reader = new CountingArchiveReader();
        var exception = await Assert.ThrowsAsync<ArchiveTooLargeException>(
            () => ArchiveManifestLoader.LoadAsync(path, _passwordStore.GetPassword()!, reader));

        Assert.Equal("Archive too large to open", exception.Message);
        Assert.Equal(0, reader.ManifestCallCount);
    }

    [Fact]
    public void ArchiveViewerLoadStateTransitionsAndArchiveChangeClearsFailuresAndPreview()
    {
        var state = new ArchiveViewerLoadState();
        state.BeginArchiveSelection();
        Assert.Equal(ArchiveContentsLoadStatus.Loading, state.ContentsStatus);
        state.MarkArchiveLoaded();
        Assert.Equal(ArchiveContentsLoadStatus.Loaded, state.ContentsStatus);

        state.MarkArchiveFailed("The password is wrong or the archive has been modified.");
        state.SetPreviewMessage("old preview failure");
        state.BeginArchiveSelection();

        Assert.Equal(ArchiveContentsLoadStatus.Loading, state.ContentsStatus);
        Assert.Equal("Loading archive contents...", state.ContentsMessage);
        Assert.Equal("Select a file to preview", state.PreviewMessage);
    }

    [Theory]
    [InlineData("too-large", "Archive too large to open")]
    [InlineData("not-found", "Archive file not found.")]
    [InlineData("denied", "Permission denied while reading the archive.")]
    [InlineData("authentication", "The password is wrong or the archive has been modified.")]
    [InlineData("truncated", "The archive is truncated or invalid.")]
    [InlineData("unknown", "Could not open the archive.")]
    public void ArchiveExceptionMessagesArePlainEnglish(string exceptionKind, string expected)
    {
        var exception = exceptionKind switch
        {
            "too-large" => new ArchiveTooLargeException(),
            "not-found" => new FileNotFoundException("raw path must not appear"),
            "denied" => new UnauthorizedAccessException("raw path must not appear"),
            "authentication" => new InvalidOperationException("AES-GCM authentication failed."),
            "truncated" => new InvalidDataException("Archive is truncated."),
            _ => new Exception("secret details must not appear")
        };

        Assert.Equal(expected, ArchiveLoadErrorMessages.ForException(exception));
    }

    private BackupOrchestrator CreateOrchestrator(
        string source,
        string destination,
        SourceScanner scanner,
        LocalScanStateIndex stateIndex,
        IArchiveWriter writer,
        IEventLogger? logger,
        string password,
        long maxFullBackupSourceBytes = BackupOrchestrator.DefaultMaxFullBackupSourceBytes,
        string? logDirectory = null) =>
        new(
            source,
            destination,
            null,
            password,
            scanner: scanner,
            writer: writer,
            reader: new CountingArchiveReader(),
            logger: logger,
            stateIndex: stateIndex,
            maxFullBackupSourceBytes: maxFullBackupSourceBytes,
            logDirectory: logDirectory);

    private async Task<(string Source, string Destination, SourceScanner Scanner, LocalScanStateIndex Index, long FileSize)> CreateSeededSourceAsync(string text)
    {
        var source = Path.Combine(_root, $"source-{Guid.NewGuid():N}");
        var destination = Path.Combine(_root, $"archives-{Guid.NewGuid():N}");
        var stateDirectory = Path.Combine(_root, $"state-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        var filePath = Path.Combine(source, "data.txt");
        var content = Encoding.UTF8.GetBytes(text);
        await File.WriteAllBytesAsync(filePath, content);
        var info = new FileInfo(filePath);
        var modified = new DateTimeOffset(info.LastWriteTimeUtc);
        modified = new DateTimeOffset(modified.UtcTicks - modified.UtcTicks % TimeSpan.TicksPerMillisecond, TimeSpan.Zero);
        var entry = new FileEntry
        {
            RelativePath = "data.txt",
            Operation = FileOperation.Added,
            FileSize = info.Length,
            LastModifiedUtc = modified,
            Sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant()
        };
        var index = new LocalScanStateIndex(source, stateDirectory);
        await index.UpdateStateAsync([entry], DateTimeOffset.UtcNow);
        var logger = new TestEventLogger();
        var scanner = new SourceScanner(source, index, logger: logger, excludedPaths: [destination]);
        return (source, destination, scanner, index, info.Length);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"eva-failure-visibility-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class CountingArchiveWriter : IArchiveWriter
    {
        public int CallCount { get; private set; }

        public Task WriteArchiveAsync(
            ArchiveManifest manifest,
            IReadOnlyCollection<FileEntry> fileEntries,
            string destinationPath,
            string password,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingArchiveWriter(Exception exception) : IArchiveWriter
    {
        public Task WriteArchiveAsync(
            ArchiveManifest manifest,
            IReadOnlyCollection<FileEntry> fileEntries,
            string destinationPath,
            string password,
            CancellationToken cancellationToken = default) => Task.FromException(exception);
    }

    private sealed class CountingArchiveReader : IArchiveReader
    {
        public int ManifestCallCount { get; private set; }

        public Task<ArchiveHeader> ReadHeaderAsync(string archivePath, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ArchiveHeader());

        public Task<ArchiveManifest> ReadManifestAsync(string archivePath, string password, CancellationToken cancellationToken = default)
        {
            ManifestCallCount++;
            return Task.FromResult(new ArchiveManifest());
        }

        public Task<byte[]> ReadFileContentAsync(string archivePath, string relativePath, string password, CancellationToken cancellationToken = default) =>
            Task.FromResult(Array.Empty<byte>());

        public Task<bool> ValidateArchiveAsync(string archivePath, string password, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class TestEventLogger : IEventLogger
    {
        public List<string> Errors { get; } = [];
        public void LogInformation(string message) { }
        public void LogWarning(string message) { }
        public void LogError(string message) => Errors.Add(message);
        public void LogException(Exception exception, string message) => Errors.Add($"{message}: {exception.GetType().Name}: {exception.Message}");
    }
}