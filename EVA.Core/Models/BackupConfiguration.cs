using System.Text.Json.Serialization;

namespace EVA.Core.Models;

public enum SnapshotFrequency
{
    Daily = 0,
    Weekly = 1,
    Monthly = 2
}

public sealed class BackupConfiguration
{
    public string SourceDirectory { get; set; } = string.Empty;
    public string PrimaryDestination { get; set; } = string.Empty;
    public string? SecondaryDestination { get; set; }
    public bool SecondaryDestinationEnabled { get; set; }
    public int BackupIntervalMinutes { get; set; } = 15;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SnapshotFrequency SnapshotFrequency { get; set; } = SnapshotFrequency.Weekly;
    public RetentionPolicy RetentionPolicy { get; set; } = new();
    public bool StartWithWindows { get; set; }
    public string? PasswordReference { get; set; }

    [JsonIgnore]
    public TimeSpan BackupInterval
    {
        get => TimeSpan.FromMinutes(BackupIntervalMinutes);
        set => BackupIntervalMinutes = value.TotalMinutes > 0 ? (int)Math.Round(value.TotalMinutes) : 0;
    }

    public static BackupConfiguration Default()
    {
        return new BackupConfiguration
        {
            SourceDirectory = string.Empty,
            PrimaryDestination = string.Empty,
            SecondaryDestination = null,
            SecondaryDestinationEnabled = false,
            BackupIntervalMinutes = 15,
            SnapshotFrequency = SnapshotFrequency.Weekly,
            RetentionPolicy = new RetentionPolicy(),
            StartWithWindows = false,
            PasswordReference = null
        };
    }
}
