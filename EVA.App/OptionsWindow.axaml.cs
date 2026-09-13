using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Controls.Notifications;
using EVA.Core.Models;
using EVA.Infrastructure;
using SukiUI.Controls;
using SukiUI.Dialogs;

namespace EVA.App;

public partial class OptionsWindow : SukiWindow
{
    private readonly PasswordStore _passwordStore = new();
    private readonly ConfigStore _configStore;
    private BackupConfiguration _configuration = BackupConfiguration.Default();
    private static ISukiDialogManager DialogManager = new SukiDialogManager();

    public OptionsWindow()
    {
        InitializeComponent();
        _configStore = new ConfigStore(passwordStore: _passwordStore);
        Loaded += OnLoaded;
        DialogHost.Manager = DialogManager;
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        await LoadConfigurationAsync();
    }

    private async Task LoadConfigurationAsync()
    {
        _configuration = await _configStore.LoadAsync();

        SourceDirectoryText.Text = _configuration.SourceDirectory ?? string.Empty;
        PrimaryDestinationText.Text = _configuration.PrimaryDestination ?? string.Empty;
        SecondaryDestinationText.Text = _configuration.SecondaryDestination ?? string.Empty;
        EnableSecondaryDestinationCheck.IsChecked = _configuration.SecondaryDestinationEnabled;

        IncrementalRetentionText.Text = _configuration.RetentionPolicy?.IncrementalRetentionDays.ToString() ?? "7";
        WeeklyRetentionText.Text = _configuration.RetentionPolicy?.WeeklySnapshotRetentionDays.ToString() ?? "365";

        bool isForever = _configuration.RetentionPolicy?.MonthlySnapshotRetentionDays is null or <= 0;
        MonthlyRetentionForeverRadio.IsChecked = isForever;
        MonthlyRetentionCustomRadio.IsChecked = !isForever;
        MonthlyRetentionDaysText.Text = isForever 
            ? string.Empty 
            : _configuration.RetentionPolicy?.MonthlySnapshotRetentionDays?.ToString() ?? string.Empty;

        BackupIntervalCombo.SelectedIndex = GetBackupIntervalIndex(_configuration.BackupIntervalMinutes);
        SnapshotFrequencyCombo.SelectedIndex = (int)_configuration.SnapshotFrequency;
        StartWithWindowsCheck.IsChecked = _configuration.StartWithWindows;
    }

    private async void Save_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var config = BuildConfigurationFromForm();
            await _configStore.SaveAsync(config);
            _configuration = config;
            Close();
        }
        catch (Exception ex)
        {
            // await ShowErrorAsync(ex.Message);
            OptionsWindow.DialogManager.CreateDialog()
                .WithTitle("An Unexpected Error Occured")
                .WithContent(ex.Message)
                .OfType(NotificationType.Error)
                .WithActionButton("OK", _ => { }, true)
                .TryShow();            
        }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void BrowseSource_Click(object? sender, RoutedEventArgs e)
    {
        var path = await PickFolderAsync("Choose source directory", SourceDirectoryText.Text);
        if (!string.IsNullOrWhiteSpace(path))
        {
            SourceDirectoryText.Text = path;
        }
    }

    private async void BrowsePrimary_Click(object? sender, RoutedEventArgs e)
    {
        var path = await PickFolderAsync("Choose primary archive directory", PrimaryDestinationText.Text);
        if (!string.IsNullOrWhiteSpace(path))
        {
            PrimaryDestinationText.Text = path;
        }
    }

    private async void BrowseSecondary_Click(object? sender, RoutedEventArgs e)
    {
        var path = await PickFolderAsync("Choose secondary archive directory", SecondaryDestinationText.Text);
        if (!string.IsNullOrWhiteSpace(path))
        {
            SecondaryDestinationText.Text = path;
        }
    }

    private async void ChangePassword_Click(object? sender, RoutedEventArgs e)
    {
        var dialog = new PasswordChangeDialog();
        await dialog.ShowDialog(this);

        // dialog has closed — check if password was set
        if (_passwordStore.HasStoredPassword())
        {
            _configuration.PasswordReference = _passwordStore.PasswordReference;
        }
    }

    private BackupConfiguration BuildConfigurationFromForm()
    {
        var config = BackupConfiguration.Default();
        config.SourceDirectory = SourceDirectoryText.Text?.Trim() ?? string.Empty;
        config.PrimaryDestination = PrimaryDestinationText.Text?.Trim() ?? string.Empty;
        config.SecondaryDestination = string.IsNullOrWhiteSpace(SecondaryDestinationText.Text) ? null : SecondaryDestinationText.Text.Trim();
        config.SecondaryDestinationEnabled = EnableSecondaryDestinationCheck.IsChecked == true;
        config.BackupIntervalMinutes = ParseMetricValue(BackupIntervalCombo.SelectedIndex, 15);
        config.SnapshotFrequency = (SnapshotFrequency)Math.Clamp(SnapshotFrequencyCombo.SelectedIndex, 0, 2);
        config.StartWithWindows = StartWithWindowsCheck.IsChecked == true;

        config.RetentionPolicy = new RetentionPolicy
        {
            IncrementalRetentionDays = ParsePositiveInt(IncrementalRetentionText.Text, 7),
            WeeklySnapshotRetentionDays = ParsePositiveInt(WeeklyRetentionText.Text, 365),
            MonthlySnapshotRetentionDays = MonthlyRetentionCustomRadio.IsChecked == true
                ? ParsePositiveInt(MonthlyRetentionDaysText.Text, 30)
                : null
        };

        return config;
    }

    private static int ParsePositiveInt(string? value, int fallback)
    {
        if (int.TryParse(value, out var result) && result > 0)
        {
            return result;
        }

        return fallback;
    }

    private static int ParseMetricValue(int selectedIndex, int fallback)
    {
        return selectedIndex switch
        {
            0 => 5,
            1 => 10,
            2 => 15,
            3 => 30,
            4 => 60,
            _ => fallback
        };
    }

    private static int GetBackupIntervalIndex(int minutes)
    {
        return minutes switch
        {
            5 => 0,
            10 => 1,
            15 => 2,
            30 => 3,
            60 => 4,
            _ => 2
        };
    }

    private async Task<string?> PickFolderAsync(string title, string? currentPath)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = await TryGetFolderAsync(currentPath)
        });

        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    private async Task<IStorageFolder?> TryGetFolderAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return await StorageProvider.TryGetFolderFromPathAsync(path.Trim());
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private async Task ShowErrorAsync(string message)
    {
        await new Window
        {
            Title = "Validation error",
            Width = 420,
            Height = 180,
            Content = new TextBlock { Text = message, Margin = new Thickness(12), TextWrapping = TextWrapping.Wrap },
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        }.ShowDialog<object?>(this);
    }
}