using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using EVA.Core.Interfaces;
using EVA.Core.Models;
using EVA.Infrastructure;
using SukiUI.Controls;
using SukiUI.Dialogs;
using Avalonia.Controls.Notifications;

namespace EVA.App;

public partial class RestoreWindow : SukiWindow
{
    private static readonly ISukiDialogManager DialogManager = new SukiDialogManager();
    private readonly string _archivePath;
    private readonly string _password;
    private readonly IArchiveRestorer _restorer;

    public RestoreWindow()
        : this(string.Empty, string.Empty)
    {
    }

    public RestoreWindow(string archivePath, string password, IArchiveRestorer? restorer = null)
    {
        InitializeComponent();
        _archivePath = archivePath;
        _password = password;
        _restorer = restorer ?? new ArchiveRestorer();
        ArchivePathText.Text = archivePath;
        DialogHost.Manager = DialogManager;
    }

    private async void Browse_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose restore directory",
            AllowMultiple = false
        });

        if (folders.Count > 0)
        {
            AlternativeDirectoryText.Text = folders[0].TryGetLocalPath();
        }
    }

    private async void Restore_Click(object? sender, RoutedEventArgs e)
    {
        var destination = AlternativeDirectoryText.Text?.Trim();
        if (string.IsNullOrWhiteSpace(destination))
        {
            ResultSummaryText.Text = "Choose an alternative restore directory.";
            return;
        }

        SetBusy(true);
        try
        {
            var preflight = await _restorer.RestoreAsync(
                _archivePath,
                destination,
                _password,
                RestoreMode.AlternativeLocation,
                dryRun: true);

            if (!preflight.Success)
            {
                ShowResult(preflight);
                return;
            }

            if (preflight.FilesToOverwrite.Count > 0)
            {
                var confirmed = await ConfirmOverwriteAsync(preflight.FilesToOverwrite);
                if (!confirmed)
                {
                    ResultSummaryText.Text = "Restore cancelled.";
                    return;
                }
            }

            var result = await _restorer.RestoreAsync(
                _archivePath,
                destination,
                _password,
                RestoreMode.AlternativeLocation,
                overwriteExisting: true);
            ShowResult(result);
        }
        catch (Exception ex)
        {
            ResultSummaryText.Text = $"Restore failed: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task<bool> ConfirmOverwriteAsync(IReadOnlyCollection<string> files)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var content = new TextBlock
        {
            Text = "The following files already exist:" + Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, files),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            MaxHeight = 280
        };

        DialogManager.CreateDialog()
            .WithTitle("Overwrite existing files?")
            .WithContent(content)
            .OfType(NotificationType.Warning)
            .WithActionButton("Cancel", _ => completion.TrySetResult(false))
            .WithActionButton("Restore", _ => completion.TrySetResult(true), true)
            .TryShow();

        return await completion.Task;
    }

    private void ShowResult(RestoreResult result)
    {
        if (!result.Success)
        {
            ResultSummaryText.Text = "Restore failed:" + Environment.NewLine + string.Join(Environment.NewLine, result.Errors);
            return;
        }

        ResultSummaryText.Text = $"Restore complete. Files restored: {result.FilesRestored.Count}. " +
                                 $"Existing files reported: {result.FilesToOverwrite.Count}.";
    }

    private void SetBusy(bool isBusy)
    {
        RestoreButton.IsEnabled = !isBusy;
        RestoreProgress.IsVisible = isBusy;
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
