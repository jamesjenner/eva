using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Forms;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using EVA.Core.Models;
using EVA.Infrastructure;

namespace EVA.App;

public partial class ShellWindow : Window
{
    public ShellWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closing += OnClosing;
        CreateTrayIcon();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
        ShowInTaskbar = false;
        Hide();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    private void CreateTrayIcon()
    {
        try
        {
            using var bitmap = SystemIcons.Shield.ToBitmap();
            var hBitmap = bitmap.GetHbitmap();
            var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap,
                IntPtr.Zero,
                Int32Rect.Empty,
                System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            TrayIcon.IconSource = source;
            DeleteObject(hBitmap);
        }
        catch
        {
            TrayIcon.Icon = SystemIcons.Application;
        }
    }

    private void CreateSnapshotNow_Click(object sender, RoutedEventArgs e)
    {
        _ = RunManualSnapshotAsync();
    }

    private void ListSnapshotContent_Click(object sender, RoutedEventArgs e)
    {
        var window = new ListSnapshotWindow();
        window.Owner = this;
        window.Show();
    }

    private void RestoreSnapshot_Click(object sender, RoutedEventArgs e)
    {
        var window = new RestoreSnapshotWindow();
        window.Owner = this;
        window.Show();
    }

    private void Options_Click(object sender, RoutedEventArgs e)
    {
        var window = new OptionsWindow();
        window.Owner = this;
        window.Show();
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        Application.Current.Shutdown();
    }

    private async Task RunManualSnapshotAsync()
    {
        var config = await App.ConfigStore.LoadAsync();
        if (!App.TryValidateConfig(config, out var error))
        {
            var options = new OptionsWindow { Owner = this };
            options.Show();
            MessageBox.Show(error ?? "Configuration is invalid.", "EVA", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var password = PasswordStore.GetPassword(config.PasswordReference);
        if (string.IsNullOrWhiteSpace(password))
        {
            MessageBox.Show("A valid encryption password is required before creating a snapshot.", "EVA", MessageBoxButton.OK, MessageBoxImage.Warning);
            var options = new OptionsWindow { Owner = this };
            options.Show();
            return;
        }

        var progress = new OperationProgressWindow("Creating snapshot...");
        progress.Owner = this;
        progress.Show();

        try
        {
            var orchestrator = new BackupOrchestrator(
                config.SourceDirectory,
                config.PrimaryDestination,
                config.SecondaryDestinationEnabled ? config.SecondaryDestination : null,
                password,
                sourceId: "eva-app");

            var result = await orchestrator.RunOnceAsync(manualSnapshot: true, cancellationToken: CancellationToken.None);

            var archivePath = result.ArchivePath ?? "(not created)";
            var size = string.Empty;
            if (!string.IsNullOrWhiteSpace(result.ArchivePath) && File.Exists(result.ArchivePath))
            {
                var info = new FileInfo(result.ArchivePath);
                size = (info.Length / 1024.0).ToString("0.## KB", CultureInfo.InvariantCulture);
            }

            progress.Close();
            var status = result.Success ? "Snapshot created successfully." : "Snapshot creation failed.";
            var primary = result.Success ? "✔" : "✖";
            var secondary = result.SecondaryCopyQueued ? "Queued" : "Not configured";

            MessageBox.Show(
                $"{status}\nArchive: {archivePath}\nSize: {size}\nPrimary destination: {primary}\nSecondary destination: {secondary}",
                "EVA",
                MessageBoxButton.OK,
                result.Success ? MessageBoxImage.Information : MessageBoxImage.Error);
        }
        catch (Exception ex)
        {
            progress.Close();
            MessageBox.Show($"Snapshot creation failed:\n{ex.Message}", "EVA", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}
