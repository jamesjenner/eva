namespace EVA.Core.Models;

public sealed class RetentionPolicy
{
    public int IncrementalArchiveRetentionDays { get; set; }
    public int WeeklySnapshotRetentionYears { get; set; }
    public int MonthlySnapshotRetentionYears { get; set; }
}
