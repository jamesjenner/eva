namespace EVA.Core.Models;

public sealed class RetentionPolicy
{
    public int IncrementalRetentionDays { get; set; } = 7;
    public int WeeklySnapshotRetentionDays { get; set; } = 365;
    public int? MonthlySnapshotRetentionDays { get; set; } = null;

}
