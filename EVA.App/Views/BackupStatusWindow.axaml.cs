using Avalonia.Interactivity;
using SukiUI.Controls;

namespace EVA.App;

public partial class BackupStatusWindow : SukiWindow
{
    public BackupStatusWindow()
        : this(Array.Empty<string>(), null, DateTimeOffset.UtcNow)
    {
    }

    public BackupStatusWindow(
        IEnumerable<string> unstableFilePaths,
        DateTimeOffset? lastSuccessfulBackup,
        DateTimeOffset nextScheduledRun,
        string? failureMessage = null)
    {
        InitializeComponent();
        Update(unstableFilePaths, lastSuccessfulBackup, nextScheduledRun, failureMessage);
        Closed += (_, _) => App.StatusWindow = null;
    }

    public void Update(
        IEnumerable<string> unstableFilePaths,
        DateTimeOffset? lastSuccessfulBackup,
        DateTimeOffset nextScheduledRun,
        string? failureMessage = null)
    {
        var paths = unstableFilePaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        FailureMessageText.Text = failureMessage ?? string.Empty;
        FailureMessageText.IsVisible = failureMessage is not null;
        StatusSummaryText.Text = $"{paths.Count} file{(paths.Count == 1 ? "" : "s")} could not be read and " +
                                 $"{(paths.Count == 1 ? "was" : "were")} skipped.";
        LastSuccessfulBackupText.Text = lastSuccessfulBackup.HasValue
            ? FormatDateTime(lastSuccessfulBackup.Value)
            : "None";
        NextScheduledRunText.Text = FormatDateTime(nextScheduledRun);
        UnstableFilesList.ItemsSource = paths;
    }

    private async void BackUpNow_Click(object? sender, RoutedEventArgs e)
    {
        BackUpNowButton.IsEnabled = false;
        try
        {
            await App.CreateSnapshotNowAsync();
        }
        finally
        {
            BackUpNowButton.IsEnabled = true;
        }
    }

    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private static string FormatDateTime(DateTimeOffset value)
    {
        return value.ToLocalTime().ToString("dd MMM yyyy HH:mm");
    }
}
