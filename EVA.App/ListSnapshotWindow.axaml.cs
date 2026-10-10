using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using EVA.Core;
using EVA.Core.Interfaces;
using EVA.Core.Models;
using EVA.Infrastructure;
using SukiUI.Controls;

namespace EVA.App;

public partial class ListSnapshotWindow : SukiWindow
{
    private readonly IArchiveReader _archiveReader = new ArchiveReader();
    private readonly IPreviewCacheService _previewCache = new PreviewCacheService(App.PreviewCacheRoot);
    private readonly ObservableCollection<ArchiveLocationViewModel> _locations = [];
    private readonly ObservableCollection<ArchiveListRowViewModel> _archiveRows = [];
    private CancellationTokenSource? _scanCancellation;
    private CancellationTokenSource? _contentsCancellation;
    private CancellationTokenSource? _previewCancellation;
    private CancellationTokenSource? _cacheBuildCancellation;
    private Task<PreviewCacheResult>? _cacheBuildTask;
    private Task? _previewTask;
    private string? _selectedPreviewArchivePath;
    private string? _activeCacheArchiveId;
    private bool _initializingLocations;

    public ListSnapshotWindow()
    {
        InitializeComponent();
        LocationSelector.ItemsSource = _locations;
        ArchiveList.ItemsSource = _archiveRows;
        Loaded += Window_Loaded;
        Closed += Window_Closed;
    }

    private async void Window_Loaded(object? sender, RoutedEventArgs e)
    {
        Loaded -= Window_Loaded;
        try
        {
            var config = await App.ConfigStore.LoadAsync();
            _initializingLocations = true;
            AddConfiguredLocation("Primary", config.PrimaryDestination);
            if (config.SecondaryDestinationEnabled)
            {
                AddConfiguredLocation("Secondary", config.SecondaryDestination);
            }

            LocationSelector.SelectedIndex = _locations.Count > 0 ? 0 : -1;
            _initializingLocations = false;

            if (LocationSelector.SelectedItem is ArchiveLocationViewModel location)
            {
                await LoadArchivesAsync(location);
            }
            else
            {
                ScanStatusText.Text = "No archive destination is configured.";
            }
        }
        catch
        {
            _initializingLocations = false;
            ScanStatusText.Text = "Archive destinations could not be loaded.";
        }
    }

    private void AddConfiguredLocation(string label, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || _locations.Any(location =>
                string.Equals(location.Directory, path, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        _locations.Add(new ArchiveLocationViewModel($"{label} - {path}", path));
    }

    private async void LocationSelector_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_initializingLocations && LocationSelector.SelectedItem is ArchiveLocationViewModel location)
        {
            await LoadArchivesAsync(location);
        }
    }

    private async void BrowseLocation_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose an archive folder",
            AllowMultiple = false,
            SuggestedStartLocation = await TryGetFolderAsync((LocationSelector.SelectedItem as ArchiveLocationViewModel)?.Directory)
        });

        var path = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var location = _locations.FirstOrDefault(item =>
            string.Equals(item.Directory, path, StringComparison.OrdinalIgnoreCase));
        if (location is null)
        {
            location = new ArchiveLocationViewModel($"Manual - {path}", path);
            _locations.Add(location);
        }

        if (ReferenceEquals(LocationSelector.SelectedItem, location))
        {
            await LoadArchivesAsync(location);
        }
        else
        {
            LocationSelector.SelectedItem = location;
        }
    }

    private async Task<IStorageFolder?> TryGetFolderAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return await StorageProvider.TryGetFolderFromPathAsync(path);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private async Task LoadArchivesAsync(ArchiveLocationViewModel location)
    {
        _scanCancellation?.Cancel();
        _contentsCancellation?.Cancel();
        _previewCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _scanCancellation = cancellation;
        var token = cancellation.Token;

        _archiveRows.Clear();
        ArchiveContentsTree.ItemsSource = null;
        ArchiveContentsTree.IsVisible = false;
        ContentsStatusText.Text = "Select an archive";
        ContentsStatusText.IsVisible = true;
        ClearPreview("Select a file to preview");
        ScanLoadingIndicator.IsVisible = true;
        ScanStatusText.Text = $"Scanning {location.Directory}...";

        try
        {
            var archivePaths = await Task.Run(
                () => Directory.Exists(location.Directory)
                    ? Directory.EnumerateFiles(location.Directory, "*.eva", SearchOption.TopDirectoryOnly).ToArray()
                    : Array.Empty<string>(),
                token);

            var unreadableHeaders = 0;
            var items = await Task.WhenAll(archivePaths.Select(async path =>
            {
                try
                {
                    var header = await Task.Run(
                        () => _archiveReader.ReadHeaderAsync(path, token), token);
                    return ArchiveListRowViewModel.ForArchive(path, header);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    Interlocked.Increment(ref unreadableHeaders);
                    return ArchiveListRowViewModel.ForUnreadableArchive(path);
                }
            }));

            token.ThrowIfCancellationRequested();
            var archiveRows = items;
            var archivesByPath = archiveRows.ToDictionary(item => item.Path, StringComparer.OrdinalIgnoreCase);
            var groups = ArchiveDateGrouping.GroupArchives(archivePaths, TimeProvider.System);
            var displayRows = ArchiveListRowViewModel.BuildRows(groups, archivesByPath);
            if (displayRows.Count(row => row.IsArchive) != archivePaths.Length)
            {
                throw new InvalidOperationException("The archive list omitted one or more archive files.");
            }

            foreach (var row in displayRows)
            {
                _archiveRows.Add(row);
            }

            var groupedArchiveCount = displayRows.Count(row => row.IsArchive);
            ScanStatusText.Text = groupedArchiveCount == 0
                ? archivePaths.Length == 0
                    ? $"No archives found in {location.Directory}."
                    : "No readable archives were found."
                : unreadableHeaders == 0
                    ? $"{groupedArchiveCount} archive(s) found."
                    : $"{groupedArchiveCount} archive(s) found; {unreadableHeaders} header(s) could not be read.";
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            if (ReferenceEquals(_scanCancellation, cancellation))
            {
                ScanStatusText.Text = "The selected archive folder could not be scanned.";
            }
        }
        finally
        {
            if (ReferenceEquals(_scanCancellation, cancellation))
            {
                _scanCancellation = null;
                ScanLoadingIndicator.IsVisible = false;
            }

            cancellation.Dispose();
        }
    }

    private async void ArchiveList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ArchiveList.SelectedItem is ArchiveListRowViewModel { IsArchive: true } archive)
        {
            await SwitchPreviewArchiveAsync(archive);
            await LoadArchiveContentsAsync(archive);
        }
    }

    private async Task SwitchPreviewArchiveAsync(ArchiveListRowViewModel archive)
    {
        if (string.Equals(_selectedPreviewArchivePath, archive.Path, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _cacheBuildCancellation?.Cancel();
        _previewCancellation?.Cancel();
        if (_previewTask is not null)
        {
            try
            {
                await _previewTask;
            }
            catch
            {
            }
        }

        if (_cacheBuildTask is not null)
        {
            try
            {
                var result = await _cacheBuildTask;
                if (!result.IsTooLarge)
                {
                    _activeCacheArchiveId = result.ArchiveId;
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
            }
        }

        if (!string.IsNullOrWhiteSpace(_activeCacheArchiveId))
        {
            try
            {
                await _previewCache.DeleteArchiveAsync(_activeCacheArchiveId);
            }
            catch
            {
            }
        }

        _cacheBuildCancellation?.Dispose();
        _cacheBuildCancellation = null;
        _cacheBuildTask = null;
        _activeCacheArchiveId = null;
        _selectedPreviewArchivePath = archive.Path;
    }

    private async Task LoadArchiveContentsAsync(ArchiveListRowViewModel archive)
    {
        _contentsCancellation?.Cancel();
        _previewCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _contentsCancellation = cancellation;
        var token = cancellation.Token;

        ArchiveContentsTree.ItemsSource = null;
        ArchiveContentsTree.IsVisible = false;
        ContentsStatusText.Text = "Loading archive contents...";
        ContentsStatusText.IsVisible = true;
        ContentsLoadingIndicator.IsVisible = true;
        ClearPreview("Select a file to preview");

        try
        {
            var password = App.PasswordStore.GetPassword();
            if (string.IsNullOrWhiteSpace(password))
            {
                ContentsStatusText.Text = "No password is stored.";
                return;
            }

            var manifest = await Task.Run(
                () => _archiveReader.ReadManifestAsync(archive.Path, password, token), token);
            token.ThrowIfCancellationRequested();
            archive.SetManual(manifest.IsManual);
            var tree = await Task.Run(
                () => ArchiveContentProjection.BuildTree(manifest.Files, manifest.Directories, manifest.Deleted), token);
            token.ThrowIfCancellationRequested();
            ArchiveContentsTree.ItemsSource = tree;
            ArchiveContentsTree.IsVisible = true;
            ContentsStatusText.IsVisible = false;
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            if (ReferenceEquals(_contentsCancellation, cancellation))
            {
                ContentsStatusText.Text = "This archive could not be decrypted or read.";
            }
        }
        finally
        {
            if (ReferenceEquals(_contentsCancellation, cancellation))
            {
                _contentsCancellation = null;
                ContentsLoadingIndicator.IsVisible = false;
            }

            cancellation.Dispose();
        }
    }

    private async void ArchiveContentsTree_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ArchiveContentsTree.SelectedItem is ArchiveContentNode node)
        {
            var task = LoadPreviewAsync(node);
            _previewTask = task;
            await task;
            if (ReferenceEquals(_previewTask, task))
            {
                _previewTask = null;
            }
        }
    }

    private async Task LoadPreviewAsync(ArchiveContentNode node)
    {
        _previewCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _previewCancellation = cancellation;
        var token = cancellation.Token;
        ClearPreview("No preview available");

        if (node.IsDirectory || node.IsDeleted || string.IsNullOrWhiteSpace(node.ArchivePath) ||
            ArchiveList.SelectedItem is not ArchiveListRowViewModel { IsArchive: true } archive)
        {
            if (ReferenceEquals(_previewCancellation, cancellation))
            {
                _previewCancellation = null;
            }

            cancellation.Dispose();
            return;
        }

        var password = App.PasswordStore.GetPassword();
        if (string.IsNullOrWhiteSpace(password))
        {
            if (ReferenceEquals(_previewCancellation, cancellation))
            {
                _previewCancellation = null;
            }

            cancellation.Dispose();
            return;
        }

        PreviewCacheResult? cacheResult = null;
        try
        {
            _cacheBuildCancellation ??= new CancellationTokenSource();
            _cacheBuildTask ??= _previewCache.EnsureCachedAsync(
                archive.Path,
                password,
                _cacheBuildCancellation.Token);
            cacheResult = await _cacheBuildTask;
            token.ThrowIfCancellationRequested();
            if (cacheResult.IsTooLarge)
            {
                ClearPreview("Archive too large to preview");
                return;
            }

            _activeCacheArchiveId = cacheResult.ArchiveId;
            ShowCacheNotice(cacheResult);
            var cachedFilePath = Path.Combine(
                cacheResult.CacheDirectory!,
                node.ArchivePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));
            var preview = await ArchiveContentProjection.CreateTextPreviewAsync(cachedFilePath, cancellationToken: token);
            if (preview.IsSupported)
            {
                PreviewTextBox.Text = preview.Text;
                PreviewTextBox.IsVisible = true;
                PreviewStatusText.IsVisible = false;
                PreviewTruncatedNotice.IsVisible = preview.IsTruncated;
            }
            else
            {
                PreviewStatusText.Text = "No preview available";
                PreviewStatusText.IsVisible = true;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            if (ReferenceEquals(_previewCancellation, cancellation))
            {
                ClearPreview("Could not create or read the archive preview.");
                if (cacheResult is not null)
                {
                    ShowCacheNotice(cacheResult);
                }
            }
        }
        finally
        {
            if (ReferenceEquals(_previewCancellation, cancellation))
            {
                _previewCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private void ClearPreview(string message)
    {
        PreviewTextBox.Text = string.Empty;
        PreviewTextBox.IsVisible = false;
        PreviewTruncatedNotice.IsVisible = false;
        PreviewCacheNotice.Text = string.Empty;
        PreviewCacheNotice.IsVisible = false;
        PreviewStatusText.Text = message;
        PreviewStatusText.IsVisible = true;
    }

    private void ShowCacheNotice(PreviewCacheResult result)
    {
        if (result.SkippedEntries.Count == 0)
        {
            PreviewCacheNotice.Text = string.Empty;
            PreviewCacheNotice.IsVisible = false;
            return;
        }

        PreviewCacheNotice.Text = $"Skipped unsupported archive paths: {string.Join(", ", result.SkippedEntries)}";
        PreviewCacheNotice.IsVisible = true;
    }

    private async void Window_Closed(object? sender, EventArgs e)
    {
        CancelPendingLoads();
        if (_previewTask is not null)
        {
            try
            {
                await _previewTask;
            }
            catch
            {
            }
        }

        if (_cacheBuildTask is not null)
        {
            try
            {
                var result = await _cacheBuildTask;
                if (!result.IsTooLarge)
                {
                    _activeCacheArchiveId = result.ArchiveId;
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
            }
        }

        if (!string.IsNullOrWhiteSpace(_activeCacheArchiveId))
        {
            try
            {
                await _previewCache.DeleteArchiveAsync(_activeCacheArchiveId);
            }
            catch
            {
            }
        }

        _cacheBuildCancellation?.Dispose();
        _cacheBuildCancellation = null;
        _cacheBuildTask = null;
    }

    private void CancelPendingLoads()
    {
        _scanCancellation?.Cancel();
        _contentsCancellation?.Cancel();
        _previewCancellation?.Cancel();
        _cacheBuildCancellation?.Cancel();
    }

}
