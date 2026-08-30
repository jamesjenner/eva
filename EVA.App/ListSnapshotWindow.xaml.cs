using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using EVA.Core.Models;
using EVA.Infrastructure;

namespace EVA.App;

public partial class ListSnapshotWindow : Window
{
    private readonly List<ArchiveViewItem> _archiveItems = new();

    public ListSnapshotWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await LoadArchiveListAsync();
    }

    private async Task LoadArchiveListAsync()
    {
        try
        {
            var config = await App.ConfigStore.LoadAsync();
            var directory = config.PrimaryDestination;
            if (!Directory.Exists(directory))
            {
                return;
            }

            var files = Directory.GetFiles(directory, "*.eva", SearchOption.TopDirectoryOnly)
                .OrderByDescending(path => File.GetLastWriteTimeUtc(path))
                .ToList();

            _archiveItems.Clear();
            foreach (var file in files)
            {
                try
                {
                    var header = await new ArchiveReader().ReadHeaderAsync(file);
                    var typeName = header.ArchiveType.ToString();
                    var size = FormatSize(new FileInfo(file).Length);
                    _archiveItems.Add(new ArchiveViewItem
                    {
                        FilePath = file,
                        CreatedUtc = header.CreatedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
                        ArchiveType = typeName,
                        ArchiveSize = size,
                        ChainStatus = "Unknown",
                        Header = header
                    });
                }
                catch
                {
                    // Skip unreadable or malformed archive files.
                }
            }

            RestorePointList.ItemsSource = _archiveItems;
            if (_archiveItems.Count > 0)
            {
                RestorePointList.SelectedIndex = 0;
            }
        }
        catch
        {
            // Ignore listing failures; the window should remain open with an empty state.
        }
    }

    private async void RestorePointList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RestorePointList.SelectedItem is not ArchiveViewItem selected)
        {
            ContentTree.Items.Clear();
            return;
        }

        try
        {
            var manifest = await new ArchiveReader().ReadManifestAsync(selected.FilePath, PasswordStore.GetPassword(PasswordStore.PasswordReference) ?? string.Empty);
            ShowManifestTree(manifest);
        }
        catch
        {
            ContentTree.Items.Clear();
            var item = new TreeViewItem { Header = "Unable to decrypt this restore point." };
            ContentTree.Items.Add(item);
        }
    }

    private void ShowManifestTree(ArchiveManifest manifest)
    {
        var root = new TreeViewItem { Header = "Root" };
        foreach (var directory in manifest.Directories.OrderBy(d => d.RelativePath))
        {
            var item = new TreeViewItem { Header = directory.RelativePath };
            root.Items.Add(item);
        }

        foreach (var file in manifest.Files.OrderBy(f => f.RelativePath))
        {
            var item = new TreeViewItem { Header = file.RelativePath };
            root.Items.Add(item);
        }

        if (root.Items.Count == 0)
        {
            root.Items.Add(new TreeViewItem { Header = "No entries available" });
        }

        ContentTree.Items.Clear();
        ContentTree.Items.Add(root);
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F1} MB";
    }

    private sealed class ArchiveViewItem
    {
        public string FilePath { get; set; } = string.Empty;
        public string CreatedUtc { get; set; } = string.Empty;
        public string ArchiveType { get; set; } = string.Empty;
        public string ArchiveSize { get; set; } = string.Empty;
        public string ChainStatus { get; set; } = string.Empty;
        public ArchiveHeader Header { get; set; } = new();
    }
}
