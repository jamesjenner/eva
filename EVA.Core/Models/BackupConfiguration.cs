namespace EVA.Core.Models;

public sealed class BackupConfiguration
{
    public string SourceDirectory { get; set; } = string.Empty;
    public string PrimaryArchiveDirectory { get; set; } = string.Empty;
    public string? SecondaryArchiveDirectory { get; set; }
    public bool EnableSecondaryDestination { get; set; }
    public TimeSpan BackupInterval { get; set; }
    public string SnapshotFrequency { get; set; } = string.Empty;
    public RetentionPolicy RetentionPolicy { get; set; } = new();
    public bool StartWithWindows { get; set; }
}
