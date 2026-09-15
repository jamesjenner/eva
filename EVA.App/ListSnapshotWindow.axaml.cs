using Avalonia.Controls;
using Avalonia.Interactivity;
using EVA.Core.Interfaces;
using EVA.Core.Models;
using EVA.Infrastructure;
using SukiUI.Controls;

namespace EVA.App;

public partial class ListSnapshotWindow : SukiWindow
{
    private readonly IArchiveReader _archiveReader = new ArchiveReader();
    private bool _syncingScroll;

    public ListSnapshotWindow()
    {
        InitializeComponent();
        Loaded += Window_Loaded;
    }

    private async void Window_Loaded(object? sender, RoutedEventArgs e)
    {
        Loaded -= Window_Loaded;
        await LoadArchivesAsync();
    }

    private async Task LoadArchivesAsync()
    {
        LoadingIndicator.IsVisible = true;
        ScanStatusText.Text = "Scanning archive destinations...";
        PrimaryArchivesList.ItemsSource = null;
        SecondaryArchivesList.ItemsSource = null;
        PrimaryMessageText.Text = string.Empty;
        SecondaryMessageText.Text = string.Empty;

        try
        {
            var config = await App.ConfigStore.LoadAsync();
            PrimaryPathText.Text = config.PrimaryDestination ?? string.Empty;
            SecondaryPathText.Text = config.SecondaryDestinationEnabled && !string.IsNullOrWhiteSpace(config.SecondaryDestination)
                ? config.SecondaryDestination
                : "Secondary destination not configured";

            var primaryTask = ScanDestinationAsync(config.PrimaryDestination);
            var secondaryTask = config.SecondaryDestinationEnabled
                ? ScanDestinationAsync(config.SecondaryDestination)
                : Task.FromResult(new ArchiveScanResult(config.SecondaryDestination, Array.Empty<ArchiveListItem>(),
                    string.IsNullOrWhiteSpace(config.SecondaryDestination)
                        ? $"Directory not found: {config.SecondaryDestination ?? string.Empty}"
                        : $"Directory not found: {config.SecondaryDestination}"));

            var results = await Task.WhenAll(primaryTask, secondaryTask);
            ApplyScanResult(results[0], PrimaryArchivesList, PrimaryMessageText);
            ApplyScanResult(results[1], SecondaryArchivesList, SecondaryMessageText);
            ScanStatusText.Text = "Archive scan complete.";
        }
        catch (Exception ex)
        {
            ScanStatusText.Text = $"Archive scan failed: {ex.Message}";
        }
        finally
        {
            LoadingIndicator.IsVisible = false;
        }
    }

    private async Task<ArchiveScanResult> ScanDestinationAsync(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return new ArchiveScanResult(directory, Array.Empty<ArchiveListItem>(),
                $"Directory not found: {directory ?? string.Empty}");
        }

        var archiveItems = new List<ArchiveListItem>();
        var headerErrors = new List<string>();
        string[] archivePaths;
        try
        {
            archivePaths = Directory.EnumerateFiles(directory, "*.eva", SearchOption.TopDirectoryOnly).ToArray();
        }
        catch
        {
            return new ArchiveScanResult(directory, Array.Empty<ArchiveListItem>(),
                $"Directory not found: {directory}");
        }

        var headerTasks = archivePaths.Select(async path =>
        {
            try
            {
                var header = await Task.Run(() => _archiveReader.ReadHeaderAsync(path));
                return new ArchiveListItem(path, header, new FileInfo(path).Length);
            }
            catch
            {
                lock (headerErrors)
                {
                    headerErrors.Add($"Archive header could not be read: {Path.GetFileName(path)}");
                }
                return null;
            }
        });

        var scannedItems = await Task.WhenAll(headerTasks);
        archiveItems.AddRange(scannedItems.Where(item => item is not null).Select(item => item!));
        archiveItems.Sort((left, right) => left.CreatedUtc.CompareTo(right.CreatedUtc));

        var message = headerErrors.Count > 0
            ? string.Join(Environment.NewLine, headerErrors)
            : archiveItems.Count == 0
                ? $"No archives found in {directory}"
                : string.Empty;
        return new ArchiveScanResult(directory, archiveItems, message);
    }

    private static void ApplyScanResult(ArchiveScanResult result, ItemsControl list, TextBlock message)
    {
        list.ItemsSource = result.Items;
        message.Text = result.Message;
    }

    private void PrimaryScrollViewer_ScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is ScrollViewer source)
        {
            SyncScroll(SecondaryScrollViewer, source.Offset.Y);
        }
    }

    private void SecondaryScrollViewer_ScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is ScrollViewer source)
        {
            SyncScroll(PrimaryScrollViewer, source.Offset.Y);
        }
    }

    private void SyncScroll(ScrollViewer target, double verticalOffset)
    {
        if (_syncingScroll || Math.Abs(target.Offset.Y - verticalOffset) < 0.5)
        {
            return;
        }

        try
        {
            _syncingScroll = true;
            target.Offset = new Avalonia.Vector(target.Offset.X, verticalOffset);
        }
        finally
        {
            _syncingScroll = false;
        }
    }

    private async void OpenManifest_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ArchiveListItem archive })
        {
            return;
        }

        ManifestPanel.IsVisible = true;
        ManifestErrorText.IsVisible = false;
        ManifestErrorText.Text = string.Empty;
        ManifestMetadataText.Text = string.Empty;
        ManifestFilesList.ItemsSource = null;
        ManifestDirectoriesList.ItemsSource = null;
        ManifestLoadingText.IsVisible = true;

        try
        {
            var password = App.PasswordStore.GetPassword();
            if (password is null)
            {
                ShowManifestError("No password is stored. Please configure EVA and try again.");
                return;
            }

            var manifest = await _archiveReader.ReadManifestAsync(archive.Path, password);
            ManifestMetadataText.Text = FormatManifestMetadata(manifest);
            ManifestFilesList.ItemsSource = manifest.Files.Select(ManifestFileItem.From).ToList();
            ManifestDirectoriesList.ItemsSource = manifest.Directories.Select(ManifestDirectoryItem.From).ToList();

            System.Diagnostics.Debug.WriteLine($"Files in manifest: {manifest.Files.Count}");
            System.Diagnostics.Debug.WriteLine($"Directories in manifest: {manifest.Directories.Count}");
            System.Diagnostics.Debug.WriteLine($"Deleted in manifest: {manifest.Deleted.Count}");            
        }
        catch
        {
            ShowManifestError("This archive was created with a different password and cannot be displayed.");
        }
        finally
        {
            ManifestLoadingText.IsVisible = false;
        }
    }

    private void ShowManifestError(string message)
    {
        ManifestErrorText.Text = message;
        ManifestErrorText.IsVisible = true;
    }

    private void CloseManifest_Click(object? sender, RoutedEventArgs e)
    {
        ManifestPanel.IsVisible = false;
    }

    private static string FormatManifestMetadata(ArchiveManifest manifest)
    {
        var parent = string.IsNullOrWhiteSpace(manifest.ParentArchiveId) ? "None" : manifest.ParentArchiveId;
        return $"Archive ID: {manifest.ArchiveId}{Environment.NewLine}" +
               $"Archive type: {manifest.ArchiveType}{Environment.NewLine}" +
               $"Created: {manifest.CreatedUtc.ToLocalTime():dd MMM yyyy HH:mm}{Environment.NewLine}" +
               $"Chain ID: {manifest.ChainId}{Environment.NewLine}" +
               $"Parent archive ID: {parent}";
    }

    private sealed record ArchiveScanResult(
        string? Directory,
        IReadOnlyList<ArchiveListItem> Items,
        string Message);

    private sealed class ArchiveListItem
    {
        public ArchiveListItem(string path, ArchiveHeader header, long size)
        {
            Path = path;
            FileName = System.IO.Path.GetFileName(path);
            TypeLabel = header.ArchiveType.ToString();
            CreatedUtc = header.CreatedUtc;
            DetailsLabel = $"{FormatSize(size)} | {header.CreatedUtc.ToLocalTime():dd MMM yyyy HH:mm}";
        }

        public string Path { get; }
        public string FileName { get; }
        public string TypeLabel { get; }
        public DateTimeOffset CreatedUtc { get; }
        public string DetailsLabel { get; }

        private static string FormatSize(long bytes)
        {
            const double kilobyte = 1024;
            const double megabyte = kilobyte * 1024;
            const double gigabyte = megabyte * 1024;

            return bytes switch
            {
                >= (long)gigabyte => $"{bytes / gigabyte:0.##} GB",
                >= (long)megabyte => $"{bytes / megabyte:0.##} MB",
                _ => $"{Math.Max(1, bytes / kilobyte):0.##} KB"
            };
        }
    }

    private sealed class ManifestFileItem
    {
        public string DetailsLabel { get; private init; } = string.Empty;

        public static ManifestFileItem From(FileEntry entry) => new()
        {
            DetailsLabel = $"{entry.RelativePath} | {entry.Operation} | {FormatSize(entry.FileSize)} | " +
                           $"{entry.LastModifiedUtc.ToLocalTime():dd MMM yyyy HH:mm}"
        };

        private static string FormatSize(long bytes) => bytes switch
        {
            >= 1024 * 1024 * 1024 => $"{bytes / (1024d * 1024 * 1024):0.##} GB",
            >= 1024 * 1024 => $"{bytes / (1024d * 1024):0.##} MB",
            _ => $"{Math.Max(1, bytes / 1024d):0.##} KB"
        };
    }

    private sealed class ManifestDirectoryItem
    {
        public string DetailsLabel { get; private init; } = string.Empty;

        public static ManifestDirectoryItem From(DirectoryEntry entry) => new()
        {
            DetailsLabel = $"{entry.RelativePath} | {entry.Operation} | " +
                           $"{(entry.LastModifiedUtc.HasValue ? entry.LastModifiedUtc.Value.ToLocalTime().ToString("dd MMM yyyy HH:mm") : "No timestamp")}"
        };
    }
}
