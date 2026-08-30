namespace EVA.Core.Models;

public sealed class RetentionPolicy
{
    public int IncrementalRetentionDays { get; set; } = 7;
    public int WeeklySnapshotRetentionDays { get; set; } = 365;
    public int? MonthlySnapshotRetentionDays { get; set; } = null;

    public int IncrementalArchiveRetentionDays
    {
        get => IncrementalRetentionDays;
        set => IncrementalRetentionDays = value;
    }

    public int WeeklySnapshotRetention
    {
        get => WeeklySnapshotRetentionDays;
        set => WeeklySnapshotRetentionDays = value;
    }

    public int? MonthlySnapshotRetention
    {
        get => MonthlySnapshotRetentionDays;
        set => MonthlySnapshotRetentionDays = value;
    }
}
