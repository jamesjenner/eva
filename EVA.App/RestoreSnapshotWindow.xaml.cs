using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using EVA.Core.Models;
using EVA.Infrastructure;

namespace EVA.App;

public partial class RestoreSnapshotWindow : Window
{
    private readonly List<RestoreArchiveItem> _items = new();

    public RestoreSnapshotWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await LoadRestorePointsAsync();
    }

    private async Task LoadRestorePointsAsync()
    {
        var config = await App.ConfigStore.LoadAsync();
        var directory = config.PrimaryDestination;
        if (!Directory.Exists(directory))
        {
            return;
        }

        _items.Clear();
        foreach (var file in Directory.GetFiles(directory, "*.eva", SearchOption.TopDirectoryOnly).OrderByDescending(p => File.GetLastWriteTimeUtc(p)))
        {
            try
            {
                var header = await new ArchiveReader().ReadHeaderAsync(file);
                _items.Add(new RestoreArchiveItem
                {
                    FilePath = file,
                    CreatedUtc = header.CreatedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
                    ArchiveType = header.ArchiveType.ToString(),
                    ChainStatus = "Inspected",
                    ArchiveHeader = header
                });
            }
            catch
            {
                // Skip unreadable archives.
            }
        }

        RestorePointList.ItemsSource = _items;
        if (_items.Count > 0)
        {
            RestorePointList.SelectedIndex = 0;
        }

        var defaultDestination = Path.Combine(Path.GetTempPath(), "EVA-Restore");
        DestinationText.Text = defaultDestination;
    }

    private void RestorePointList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RestorePointList.SelectedItem is not RestoreArchiveItem item)
        {
            return;
        }

        SelectedPointText.Text = $"{item.CreatedUtc} • {item.ArchiveType} • {item.ChainStatus}";
        WarningText.Text = "Destination must not be the configured source directory or a child of it.";
    }

    private void BrowseDestination_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select a restore destination",
            ShowNewFolderButton = true
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            DestinationText.Text = dialog.SelectedPath;
        }
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (RestorePointList.SelectedItem is not RestoreArchiveItem selected)
        {
            System.Windows.MessageBox.Show("Select a restore point before starting the restore.", "EVA", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var destination = DestinationText.Text.Trim();
        if (string.IsNullOrWhiteSpace(destination))
        {
            System.Windows.MessageBox.Show("Choose a restore destination.", "EVA", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var config = await App.ConfigStore.LoadAsync();
        if (string.Equals(Path.GetFullPath(destination), Path.GetFullPath(config.SourceDirectory), StringComparison.OrdinalIgnoreCase)
            || destination.StartsWith(Path.GetFullPath(config.SourceDirectory), StringComparison.OrdinalIgnoreCase))
        {
            System.Windows.MessageBox.Show("Restore destination cannot be the source directory or a child directory of it.", "EVA", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (ConfirmRestoreCheck.IsChecked != true)
        {
            System.Windows.MessageBox.Show("You must confirm before beginning restore.", "EVA", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var password = PasswordStore.GetPassword(config.PasswordReference);
        if (string.IsNullOrWhiteSpace(password))
        {
            System.Windows.MessageBox.Show("A valid password is required to restore this archive.", "EVA", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var manifest = await new ArchiveReader().ReadManifestAsync(selected.FilePath, password);
            Directory.CreateDirectory(destination);
            var root = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in manifest.Files)
            {
                var filePath = Path.Combine(destination, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                var directoryPath = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrWhiteSpace(directoryPath))
                {
                    Directory.CreateDirectory(directoryPath);
                }
                File.WriteAllText(filePath, "[restore placeholder: encrypted payload not extracted in this build]");
                root.Add(file.RelativePath);
            }

            foreach (var directory in manifest.Directories)
            {
                var target = Path.Combine(destination, directory.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(target);
            }

            System.Windows.MessageBox.Show($"Restore simulated successfully for: {selected.FilePath}\nOutput: {destination}", "EVA", MessageBoxButton.OK, MessageBoxImage.Information);
            Close();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Restore failed:\n{ex.Message}", "EVA", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private sealed class RestoreArchiveItem
    {
        public string FilePath { get; set; } = string.Empty;
        public string CreatedUtc { get; set; } = string.Empty;
        public string ArchiveType { get; set; } = string.Empty;
        public string ChainStatus { get; set; } = string.Empty;
        public ArchiveHeader ArchiveHeader { get; set; } = new();
    }
}
