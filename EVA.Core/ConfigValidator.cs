using EVA.Core.Interfaces;
using EVA.Core.Models;

namespace EVA.Core;

public sealed class ConfigValidator : IConfigValidator
{
    private readonly IPasswordStore _passwordStore;

    public ConfigValidator(IPasswordStore passwordStore)
    {
        _passwordStore = passwordStore ?? throw new ArgumentNullException(nameof(passwordStore));
    }

    public void Validate(BackupConfiguration configuration)
    {
        if (configuration is null)
        {
            throw new ArgumentNullException(nameof(configuration));
        }

        if (string.IsNullOrWhiteSpace(configuration.SourceDirectory))
        {
            throw new ArgumentException("Source directory is required.", nameof(configuration));
        }

        if (!Directory.Exists(configuration.SourceDirectory))
        {
            throw new ArgumentException($"Source directory does not exist: {configuration.SourceDirectory}", nameof(configuration));
        }

        if (!CanReadDirectory(configuration.SourceDirectory))
        {
            throw new ArgumentException($"Source directory is not readable: {configuration.SourceDirectory}", nameof(configuration));
        }

        if (string.IsNullOrWhiteSpace(configuration.PrimaryDestination))
        {
            throw new ArgumentException("Primary destination is required.", nameof(configuration));
        }

        if (!EnsureDirectoryAvailable(configuration.PrimaryDestination, "primary"))
        {
            throw new ArgumentException($"Primary destination cannot be created or written: {configuration.PrimaryDestination}", nameof(configuration));
        }

        if (!CanWriteDirectory(configuration.PrimaryDestination))
        {
            throw new ArgumentException($"Primary destination is not writable: {configuration.PrimaryDestination}", nameof(configuration));
        }

        if (configuration.SecondaryDestinationEnabled)
        {
            if (string.IsNullOrWhiteSpace(configuration.SecondaryDestination))
            {
                throw new ArgumentException("Secondary destination is required when secondary destination is enabled.", nameof(configuration));
            }

            if (!EnsureDirectoryAvailable(configuration.SecondaryDestination, "secondary"))
            {
                throw new ArgumentException($"Secondary destination cannot be created or written: {configuration.SecondaryDestination}", nameof(configuration));
            }
        }

        if (!_passwordStore.HasStoredPassword())
        {
            throw new ArgumentException("Encryption configuration is required.", nameof(configuration));
        }

        if (configuration.BackupIntervalMinutes <= 0)
        {
            throw new ArgumentException("Backup interval must be greater than zero minutes.", nameof(configuration));
        }

        if (!Enum.IsDefined(typeof(SnapshotFrequency), configuration.SnapshotFrequency))
        {
            throw new ArgumentException("Snapshot frequency is invalid.", nameof(configuration));
        }

        if (configuration.RetentionPolicy is null)
        {
            throw new ArgumentException("Retention policy is required.", nameof(configuration));
        }

        if (configuration.RetentionPolicy.IncrementalRetentionDays <= 0)
        {
            throw new ArgumentException("Incremental retention period must be greater than zero.", nameof(configuration));
        }

        if (configuration.RetentionPolicy.WeeklySnapshotRetentionDays <= 0)
        {
            throw new ArgumentException("Weekly snapshot retention period must be greater than zero.", nameof(configuration));
        }

        if (configuration.RetentionPolicy.MonthlySnapshotRetentionDays is not null && configuration.RetentionPolicy.MonthlySnapshotRetentionDays <= 0)
        {
            throw new ArgumentException("Monthly snapshot retention period must be greater than zero when configured.", nameof(configuration));
        }

        if (IsInsidePath(configuration.SourceDirectory, configuration.PrimaryDestination))
        {
            throw new ArgumentException("Source directory cannot be inside the primary destination; this creates a recursive backup relationship.", nameof(configuration));
        }

        if (IsInsidePath(configuration.PrimaryDestination, configuration.SourceDirectory))
        {
            throw new ArgumentException("Primary destination cannot be inside the source directory; this creates a recursive backup relationship.", nameof(configuration));
        }

        if (configuration.SecondaryDestinationEnabled && !string.IsNullOrWhiteSpace(configuration.SecondaryDestination))
        {
            if (IsInsidePath(configuration.SourceDirectory, configuration.SecondaryDestination))
            {
                throw new ArgumentException("Source directory cannot be inside the secondary destination; this creates a recursive backup relationship.", nameof(configuration));
            }

            if (IsInsidePath(configuration.SecondaryDestination, configuration.SourceDirectory))
            {
                throw new ArgumentException("Secondary destination cannot be inside the source directory; this creates a recursive backup relationship.", nameof(configuration));
            }
        }
    }

    private static bool CanReadDirectory(string directory)
    {
        try
        {
            var all = Directory.EnumerateFileSystemEntries(directory);
            _ = all.GetEnumerator();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool CanWriteDirectory(string directory)
    {
        try
        {
            var attributes = File.GetAttributes(directory);
            if ((attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
            {
                return false;
            }

            var testFile = Path.Combine(directory, $".eva-write-test-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(testFile, "ok");
            File.Delete(testFile);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool EnsureDirectoryAvailable(string path, string label)
    {
        try
        {
            var directory = Path.GetFullPath(path);
            if (File.Exists(directory))
            {
                return false;
            }

            Directory.CreateDirectory(directory);
            return CanWriteDirectory(directory);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsInsidePath(string candidate, string container)
    {
        var fullCandidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullContainer = Path.GetFullPath(container).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return fullCandidate.StartsWith(fullContainer, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(fullCandidate, fullContainer, StringComparison.OrdinalIgnoreCase);
    }
}
