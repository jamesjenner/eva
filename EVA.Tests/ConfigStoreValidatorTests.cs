using System.Text;
using EVA.Core;
using EVA.Core.Models;
using EVA.Infrastructure;
using Xunit;

namespace EVA.Tests;

public sealed class ConfigStoreTests
{
    [Fact]
    public async Task DefaultConfigurationIsReturnedWhenNoConfigFileExists()
    {
        var root = CreateTempRoot();
        var store = new ConfigStore(root);

        var result = await store.LoadAsync();

        Assert.NotNull(result);
        Assert.NotNull(result.RetentionPolicy);
        Assert.Equal(15, result.BackupIntervalMinutes);
        Assert.Equal(SnapshotFrequency.Weekly, result.SnapshotFrequency);
    }

    [Fact]
    public async Task AValidConfigurationIsPersistedAndReloadedCorrectly()
    {
        var root = CreateTempRoot();
        var store = new ConfigStore(root);
        var config = new BackupConfiguration
        {
            SourceDirectory = Path.Combine(root, "source"),
            PrimaryDestination = Path.Combine(root, "primary"),
            SecondaryDestination = Path.Combine(root, "secondary"),
            SecondaryDestinationEnabled = true,
            BackupIntervalMinutes = 30,
            SnapshotFrequency = SnapshotFrequency.Daily,
            RetentionPolicy = new RetentionPolicy { IncrementalRetentionDays = 14, WeeklySnapshotRetentionDays = 180, MonthlySnapshotRetentionDays = null },
            StartWithWindows = true,
            PasswordReference = "credential:eva-test"
        };

        Directory.CreateDirectory(config.SourceDirectory);
        Directory.CreateDirectory(config.PrimaryDestination);
        Directory.CreateDirectory(config.SecondaryDestination);

        await store.SaveAsync(config);
        var reloaded = await store.LoadAsync();

        Assert.NotNull(reloaded);
        Assert.Equal(config.SourceDirectory, reloaded.SourceDirectory);
        Assert.Equal(config.PrimaryDestination, reloaded.PrimaryDestination);
        Assert.Equal(config.SecondaryDestination, reloaded.SecondaryDestination);
        Assert.Equal(config.BackupIntervalMinutes, reloaded.BackupIntervalMinutes);
        Assert.Equal(config.SnapshotFrequency, reloaded.SnapshotFrequency);
        Assert.Equal(config.PasswordReference, reloaded.PasswordReference);
    }

    [Fact]
    public async Task ACorruptConfigurationFileFallsBackToDefaultsWithoutThrowing()
    {
        var root = CreateTempRoot();
        var configPath = Path.Combine(root, "eva-config.json");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(configPath, "{ invalid json");

        var store = new ConfigStore(root);
        var loaded = await store.LoadAsync();

        Assert.NotNull(loaded);
        Assert.Equal(15, loaded.BackupIntervalMinutes);
        Assert.Equal(SnapshotFrequency.Weekly, loaded.SnapshotFrequency);
    }

    [Fact]
    public async Task PlaintextPasswordIsNeverPresentInPersistedJsonFile()
    {
        var root = CreateTempRoot();
        var store = new ConfigStore(root);
        var config = new BackupConfiguration
        {
            SourceDirectory = Path.Combine(root, "source"),
            PrimaryDestination = Path.Combine(root, "primary"),
            SecondaryDestinationEnabled = false,
            BackupIntervalMinutes = 15,
            SnapshotFrequency = SnapshotFrequency.Weekly,
            PasswordReference = "credential:eva-local"
        };

        Directory.CreateDirectory(config.SourceDirectory);
        Directory.CreateDirectory(config.PrimaryDestination);

        await store.SaveAsync(config);

        var json = await File.ReadAllTextAsync(store.ConfigPath);
        Assert.DoesNotContain("Password123!", json, StringComparison.Ordinal);
        Assert.DoesNotContain("plaintext", json, StringComparison.OrdinalIgnoreCase);
    }

    private static string CreateTempRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "eva-config-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}

public sealed class ConfigValidatorTests
{
    [Fact]
    public void AFullyValidConfigurationPassesValidation()
    {
        var config = CreateValidConfiguration();

        var validator = new ConfigValidator();

        var ex = Record.Exception(() => validator.Validate(config));

        Assert.Null(ex);
    }

    [Fact]
    public void AMissingSourceDirectoryFailsValidationWithADescriptiveError()
    {
        var config = CreateValidConfiguration();
        config.SourceDirectory = Path.Combine(Path.GetTempPath(), "eva-missing-source" + Guid.NewGuid().ToString("N"));

        var validator = new ConfigValidator();

        var ex = Assert.Throws<ArgumentException>(() => validator.Validate(config));
        Assert.Contains("source", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ANonWritablePrimaryDestinationFailsValidation()
    {
        var root = CreateTempRoot();
        var config = CreateValidConfiguration();
        var locked = Path.Combine(root, "not-writable-primary");
        Directory.CreateDirectory(locked);
        File.SetAttributes(locked, FileAttributes.ReadOnly);
        config.PrimaryDestination = locked;

        var validator = new ConfigValidator();
        var ex = Assert.ThrowsAny<ArgumentException>(() => validator.Validate(config));
        Assert.NotNull(ex);

        File.SetAttributes(locked, FileAttributes.Normal);
    }

    [Fact]
    public void ASourceDirectoryInsideThePrimaryDestinationFailsValidation()
    {
        var root = CreateTempRoot();
        var source = Path.Combine(root, "source");
        var primary = Path.Combine(source, "archives");
        Directory.CreateDirectory(primary);

        var config = CreateValidConfiguration();
        config.SourceDirectory = source;
        config.PrimaryDestination = primary;

        var validator = new ConfigValidator();

        var ex = Assert.Throws<ArgumentException>(() => validator.Validate(config));
        Assert.Contains("recursive", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void APrimaryDestinationInsideTheSourceDirectoryFailsValidation()
    {
        var root = CreateTempRoot();
        var source = Path.Combine(root, "source");
        var primary = Path.Combine(source, "archives");
        Directory.CreateDirectory(source);

        var config = CreateValidConfiguration();
        config.SourceDirectory = source;
        config.PrimaryDestination = primary;

        var validator = new ConfigValidator();

        var ex = Assert.Throws<ArgumentException>(() => validator.Validate(config));
        Assert.Contains("recursive", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ASecondaryDestinationThatCannotBeCreatedFailsValidationWhenSecondaryIsEnabled()
    {
        var root = CreateTempRoot();
        var source = Path.Combine(root, "source");
        Directory.CreateDirectory(source);
        var secondary = Path.Combine(root, "secondary-file.txt");
        File.WriteAllText(secondary, "not a directory");

        var config = CreateValidConfiguration();
        config.SourceDirectory = source;
        config.PrimaryDestination = Path.Combine(root, "primary");
        config.SecondaryDestination = secondary;
        config.SecondaryDestinationEnabled = true;

        var validator = new ConfigValidator();

        var ex = Assert.Throws<ArgumentException>(() => validator.Validate(config));
        Assert.Contains("secondary", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ABackupIntervalOfZeroFailsValidation()
    {
        var config = CreateValidConfiguration();
        config.BackupIntervalMinutes = 0;

        var validator = new ConfigValidator();

        var ex = Assert.Throws<ArgumentException>(() => validator.Validate(config));
        Assert.Contains("backup interval", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ARetentionPeriodOfZeroForIncrementalsFailsValidation()
    {
        var config = CreateValidConfiguration();
        config.RetentionPolicy.IncrementalRetentionDays = 0;

        var validator = new ConfigValidator();

        var ex = Assert.Throws<ArgumentException>(() => validator.Validate(config));
        Assert.Contains("incremental", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AValidConfigurationWithSecondaryDestinationDisabledPassesEvenWhenSecondaryPathIsInvalid()
    {
        var config = CreateValidConfiguration();
        config.SecondaryDestinationEnabled = false;
        config.SecondaryDestination = Path.Combine(Path.GetTempPath(), "invalid-secondary" + Guid.NewGuid().ToString("N"));

        var validator = new ConfigValidator();

        var ex = Record.Exception(() => validator.Validate(config));
        Assert.Null(ex);
    }

    private static BackupConfiguration CreateValidConfiguration()
    {
        var root = CreateTempRoot();
        var source = Path.Combine(root, "source");
        var primary = Path.Combine(root, "primary");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(primary);

        return new BackupConfiguration
        {
            SourceDirectory = source,
            PrimaryDestination = primary,
            SecondaryDestination = Path.Combine(root, "secondary"),
            SecondaryDestinationEnabled = false,
            BackupIntervalMinutes = 15,
            SnapshotFrequency = SnapshotFrequency.Weekly,
            RetentionPolicy = new RetentionPolicy
            {
                IncrementalRetentionDays = 7,
                WeeklySnapshotRetentionDays = 365,
                MonthlySnapshotRetentionDays = null
            },
            PasswordReference = "credential:eva-valid",
            StartWithWindows = false
        };
    }

    private static string CreateTempRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "eva-config-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
