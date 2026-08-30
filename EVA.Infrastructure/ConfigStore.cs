using System.Text.Json;
using EVA.Core;
using EVA.Core.Interfaces;
using EVA.Core.Models;

namespace EVA.Infrastructure;

public sealed class ConfigStore : IConfigStore
{
    private readonly string _appDataDirectory;
    private readonly string _configPath;
    private readonly ConfigValidator _validator;

    public ConfigStore(string? appDataDirectory = null)
    {
        _appDataDirectory = string.IsNullOrWhiteSpace(appDataDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EVA")
            : Path.GetFullPath(appDataDirectory);

        Directory.CreateDirectory(_appDataDirectory);
        _configPath = Path.Combine(_appDataDirectory, "eva-config.json");
        _validator = new ConfigValidator();
    }

    public string ConfigPath => _configPath;

    public async Task<BackupConfiguration> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(_configPath))
        {
            return BackupConfiguration.Default();
        }

        try
        {
            var json = await File.ReadAllTextAsync(_configPath, cancellationToken).ConfigureAwait(false);
            var config = JsonSerializer.Deserialize<BackupConfiguration>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (config is null)
            {
                return BackupConfiguration.Default();
            }

            ApplyDefaults(config);
            return config;
        }
        catch
        {
            return BackupConfiguration.Default();
        }
    }

    public async Task SaveAsync(BackupConfiguration configuration, CancellationToken cancellationToken = default)
    {
        if (configuration is null)
        {
            throw new ArgumentNullException(nameof(configuration));
        }

        ApplyDefaults(configuration);
        _validator.Validate(configuration);

        var safeConfig = new BackupConfiguration
        {
            SourceDirectory = configuration.SourceDirectory,
            PrimaryDestination = configuration.PrimaryDestination,
            SecondaryDestination = configuration.SecondaryDestination,
            SecondaryDestinationEnabled = configuration.SecondaryDestinationEnabled,
            BackupIntervalMinutes = configuration.BackupIntervalMinutes,
            SnapshotFrequency = configuration.SnapshotFrequency,
            RetentionPolicy = configuration.RetentionPolicy ?? new RetentionPolicy(),
            StartWithWindows = configuration.StartWithWindows,
            PasswordReference = configuration.PasswordReference
        };

        var json = JsonSerializer.Serialize(safeConfig, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        await File.WriteAllTextAsync(_configPath, json, cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> ExistsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(File.Exists(_configPath));
    }

    public async Task<BackupConfiguration> UpdateAsync(BackupConfiguration configuration, CancellationToken cancellationToken = default)
    {
        if (configuration is null)
        {
            throw new ArgumentNullException(nameof(configuration));
        }

        ApplyDefaults(configuration);
        _validator.Validate(configuration);
        await SaveAsync(configuration, cancellationToken).ConfigureAwait(false);
        return configuration;
    }

    private static void ApplyDefaults(BackupConfiguration configuration)
    {
        configuration.RetentionPolicy ??= new RetentionPolicy();
        configuration.RetentionPolicy.IncrementalRetentionDays = configuration.RetentionPolicy.IncrementalRetentionDays <= 0
            ? 7
            : configuration.RetentionPolicy.IncrementalRetentionDays;

        configuration.RetentionPolicy.WeeklySnapshotRetentionDays = configuration.RetentionPolicy.WeeklySnapshotRetentionDays <= 0
            ? 365
            : configuration.RetentionPolicy.WeeklySnapshotRetentionDays;

        if (configuration.BackupIntervalMinutes <= 0)
        {
            configuration.BackupIntervalMinutes = 15;
        }

        if (!Enum.IsDefined(typeof(SnapshotFrequency), configuration.SnapshotFrequency))
        {
            configuration.SnapshotFrequency = SnapshotFrequency.Weekly;
        }
    }
}
