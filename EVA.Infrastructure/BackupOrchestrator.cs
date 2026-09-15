using System.Globalization;
using EVA.Core.Interfaces;
using EVA.Core.Models;

namespace EVA.Infrastructure;

public sealed class BackupRunResult
{
    public bool CreatedArchive { get; set; }
    public string? ArchivePath { get; set; }
    public ArchiveType ArchiveType { get; set; }
    public bool SecondaryCopyQueued { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }
    public IReadOnlyCollection<FileEntry> UnstableFiles { get; set; } = Array.Empty<FileEntry>();
    public DateTimeOffset? LastSuccessfulBackupUtc { get; set; }
}

public sealed class BackupOrchestrator
{
    private readonly string _sourceDirectory;
    private readonly string _primaryArchiveDirectory;
    private readonly string? _secondaryArchiveDirectory;
    private readonly string _password;
    private readonly SourceScanner _scanner;
    private readonly IArchiveWriter _writer;
    private readonly IArchiveReader _reader;
    private readonly IArchiveValidator _validator;
    private readonly IEventLogger _logger;
    private readonly LocalScanStateIndex _stateIndex;
    private readonly BackupScheduler _scheduler;
    private readonly SecondaryDestinationRetryQueue _retryQueue;
    private readonly IRetentionEvaluator? _retentionEvaluator;
    private readonly string _sourceId;
    private string _currentChainId = Guid.NewGuid().ToString("N");
    private DateTimeOffset? _lastSuccessfulBackupUtc;

    public BackupOrchestrator(
        string sourceDirectory,
        string primaryArchiveDirectory,
        string? secondaryArchiveDirectory,
        string password,
        SourceScanner? scanner = null,
        IArchiveWriter? writer = null,
        IArchiveReader? reader = null,
        IArchiveValidator? validator = null,
        IEventLogger? logger = null,
        LocalScanStateIndex? stateIndex = null,
        BackupScheduler? scheduler = null,
        SecondaryDestinationRetryQueue? retryQueue = null,
        IRetentionEvaluator? retentionEvaluator = null,
        string? sourceId = null)
    {
        if (string.IsNullOrWhiteSpace(sourceDirectory))
        {
            throw new ArgumentException("Source directory is required.", nameof(sourceDirectory));
        }

        if (string.IsNullOrWhiteSpace(primaryArchiveDirectory))
        {
            throw new ArgumentException("Primary archive directory is required.", nameof(primaryArchiveDirectory));
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            throw new ArgumentException("Password is required.", nameof(password));
        }

        _sourceDirectory = Path.GetFullPath(sourceDirectory);
        _primaryArchiveDirectory = Path.GetFullPath(primaryArchiveDirectory);
        _secondaryArchiveDirectory = string.IsNullOrWhiteSpace(secondaryArchiveDirectory) ? null : Path.GetFullPath(secondaryArchiveDirectory);
        _password = password;
        var excludedPaths = new List<string>();
        if (!string.IsNullOrWhiteSpace(_primaryArchiveDirectory))
        {
            excludedPaths.Add(_primaryArchiveDirectory);
        }
        if (!string.IsNullOrWhiteSpace(_secondaryArchiveDirectory))
        {
            excludedPaths.Add(_secondaryArchiveDirectory);
        }

        _scanner = scanner ?? new SourceScanner(_sourceDirectory, stateIndex ?? new LocalScanStateIndex(_sourceDirectory), excludedPaths: excludedPaths);
        _writer = writer ?? new ArchiveWriter();
        _reader = reader ?? new ArchiveReader();
        _validator = validator ?? new ArchiveValidator();
        _logger = logger ?? new NullEventLogger();
        _stateIndex = stateIndex ?? new LocalScanStateIndex(_sourceDirectory);
        _scheduler = scheduler ?? new BackupScheduler();
        _retryQueue = retryQueue ?? new SecondaryDestinationRetryQueue();
        _retentionEvaluator = retentionEvaluator;
        _sourceId = sourceId ?? Guid.NewGuid().ToString("N");

        Directory.CreateDirectory(_primaryArchiveDirectory);
        if (!string.IsNullOrWhiteSpace(_secondaryArchiveDirectory))
        {
            try
            {
                Directory.CreateDirectory(_secondaryArchiveDirectory);
            }
            catch (IOException)
            {
                // The secondary destination may already exist as a file or be otherwise unusable.
                // If so, the copy attempt will fail at runtime and be queued for retry without
                // preventing the primary archive from being created.
            }
        }
    }

    public async Task<BackupRunResult> RunOnceAsync(
        DateTimeOffset? currentCheckTime = null,
        bool manualSnapshot = false,
        CancellationToken cancellationToken = default)
    {
        var checkTime = currentCheckTime ?? DateTimeOffset.UtcNow;
        var scanResult = await _scanner.ScanAsync(_sourceDirectory, cancellationToken).ConfigureAwait(false);
        var changesExist = scanResult.ConfirmedChanges.Count > 0 || scanResult.EmptyDirectories.Count > 0;

        if (!manualSnapshot && !changesExist)
        {
            _logger.LogInformation($"No changes detected in {_sourceDirectory}; no archive created.");
            return new BackupRunResult
            {
                CreatedArchive = false,
                Success = true,
                ArchiveType = ArchiveType.Incremental,
                UnstableFiles = scanResult.UnstableFiles.ToList(),
                LastSuccessfulBackupUtc = _lastSuccessfulBackupUtc
            };
        }

        var archiveType = _scheduler.DetermineArchiveType(
            checkTime,
            hasChanges: changesExist,
            manualSnapshotRequested: manualSnapshot,
            lastSnapshotUtc: null);

        if (!manualSnapshot && archiveType == ArchiveType.Incremental && !changesExist)
        {
            return new BackupRunResult
            {
                CreatedArchive = false,
                Success = true,
                ArchiveType = ArchiveType.Incremental,
                UnstableFiles = scanResult.UnstableFiles.ToList(),
                LastSuccessfulBackupUtc = _lastSuccessfulBackupUtc
            };
        }

        if (manualSnapshot)
        {
            _currentChainId = Guid.NewGuid().ToString("N");
        }

        var manifest = CreateManifest(checkTime, archiveType, _currentChainId, manualSnapshot);
        List<FileEntry> fileEntries = archiveType == ArchiveType.Snapshot
            ? scanResult.StableFiles.Concat(scanResult.ConfirmedChanges).ToList()
            : scanResult.ConfirmedChanges.ToList();
        var directoryEntries = scanResult.EmptyDirectories.ToList();
        var archivePath = CreateArchivePath(checkTime, archiveType);

        try
        {
            await _writer.WriteArchiveAsync(manifest, fileEntries!, directoryEntries, archivePath, _password, cancellationToken).ConfigureAwait(false);
            if (!await _reader.ValidateArchiveAsync(archivePath, _password, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("Archive verification failed after write.");
            }

            if (!string.IsNullOrWhiteSpace(_secondaryArchiveDirectory))
            {
                var secondaryPath = Path.Combine(_secondaryArchiveDirectory, Path.GetFileName(archivePath));
                try
                {
                    Directory.CreateDirectory(_secondaryArchiveDirectory);
                    File.Copy(archivePath, secondaryPath, overwrite: true);
                    _logger.LogInformation($"Archive copied to secondary destination: {secondaryPath}");
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Secondary destination copy failed for {archivePath}: {ex.Message}");
                    await _retryQueue.EnqueueAsync(archivePath, cancellationToken).ConfigureAwait(false);
                }
            }

            await _stateIndex.UpdateStateAsync(fileEntries!, checkTime, cancellationToken).ConfigureAwait(false);
            _retentionEvaluator?.ApplyRetentionAsync(_primaryArchiveDirectory, cancellationToken).GetAwaiter().GetResult();
            _logger.LogInformation($"Archive created successfully: {archivePath}");
            _lastSuccessfulBackupUtc = checkTime;

            return new BackupRunResult
            {
                CreatedArchive = true,
                ArchivePath = archivePath,
                ArchiveType = archiveType,
                SecondaryCopyQueued = !string.IsNullOrWhiteSpace(_secondaryArchiveDirectory),
                Success = true,
                UnstableFiles = scanResult.UnstableFiles.ToList(),
                LastSuccessfulBackupUtc = _lastSuccessfulBackupUtc
            };
        }
        catch (Exception ex)
        {
            _logger.LogException(ex, $"Primary archive creation failed for {_sourceDirectory}");
            return new BackupRunResult
            {
                CreatedArchive = false,
                Success = false,
                Error = ex.Message,
                ArchiveType = archiveType,
                UnstableFiles = scanResult.UnstableFiles.ToList(),
                LastSuccessfulBackupUtc = _lastSuccessfulBackupUtc
            };
        }
    }

    public async Task StartAsync(
        TimeSpan? interval = null,
        CancellationToken cancellationToken = default,
        Func<BackupRunResult, Task>? resultHandler = null)
    {
        var period = interval ?? TimeSpan.FromMinutes(15);
        using var timer = new PeriodicTimer(period);
        while (!cancellationToken.IsCancellationRequested)
        {
            await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);
            var result = await RunOnceAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            if (resultHandler is not null)
            {
                await resultHandler(result).ConfigureAwait(false);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    private ArchiveManifest CreateManifest(DateTimeOffset checkTime, ArchiveType archiveType, string chainId, bool manualSnapshot)
    {
        var archiveId = Guid.NewGuid().ToString("N");
        var normalizedCheckTime = NormalizeUtcTimestamp(checkTime);
        return new ArchiveManifest
        {
            FormatVersion = 1,
            ArchiveId = archiveId,
            ArchiveType = archiveType,
            ChainId = chainId,
            SourceId = _sourceId,
            CreatedUtc = normalizedCheckTime,
            Files = new List<FileEntry>(),
            Directories = new List<DirectoryEntry>(),
            Deleted = new List<DeletedEntry>(),
            ParentArchiveId = manualSnapshot ? null : null
        };
    }

    private string CreateArchivePath(DateTimeOffset checkTime, ArchiveType archiveType)
    {
        var stamp = NormalizeUtcTimestamp(checkTime).ToUniversalTime().ToString("yyyy_MM_dd_HHmmss", CultureInfo.InvariantCulture);
        var sequence = DateTime.UtcNow.Ticks % 100;
        var fileName = $"{stamp}_{sequence:00}.eva";
        return Path.Combine(_primaryArchiveDirectory, fileName);
    }

    private static DateTimeOffset NormalizeUtcTimestamp(DateTimeOffset value)
    {
        var utcTicks = value.UtcTicks;
        var millisecondTicks = TimeSpan.TicksPerMillisecond;
        var truncatedTicks = utcTicks - (utcTicks % millisecondTicks);
        return new DateTimeOffset(truncatedTicks, TimeSpan.Zero);
    }

    private sealed class NullEventLogger : IEventLogger
    {
        public void LogInformation(string message) { }
        public void LogWarning(string message) { }
        public void LogError(string message) { }
        public void LogException(Exception exception, string message) { }
    }
}
