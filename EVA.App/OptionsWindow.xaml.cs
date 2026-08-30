using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using EVA.Core;
using EVA.Core.Models;

namespace EVA.App;

public partial class OptionsWindow : Window
{
    private readonly ConfigValidator _validator = new();

    public OptionsWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        MonthlyRetentionCombo.SelectionChanged += MonthlyRetentionCombo_SelectionChanged;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var config = await App.ConfigStore.LoadAsync();
        ApplyConfiguration(config);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var config = BuildConfiguration();
            _validator.Validate(config);
            App.ConfigStore.SaveAsync(config).GetAwaiter().GetResult();
            _ = App.RefreshOrchestratorAsync(config);
            Close();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "EVA Options", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void BrowseSource_Click(object sender, RoutedEventArgs e)
    {
        var path = PickDirectory();
        if (!string.IsNullOrWhiteSpace(path))
        {
            SourceDirectoryText.Text = path;
        }
    }

    private void BrowsePrimary_Click(object sender, RoutedEventArgs e)
    {
        var path = PickDirectory();
        if (!string.IsNullOrWhiteSpace(path))
        {
            PrimaryDestinationText.Text = path;
        }
    }

    private void BrowseSecondary_Click(object sender, RoutedEventArgs e)
    {
        var path = PickDirectory();
        if (!string.IsNullOrWhiteSpace(path))
        {
            SecondaryDestinationText.Text = path;
        }
    }

    private void ChangePassword_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new PasswordChangeDialog();
        dialog.Owner = this;
        dialog.ShowDialog();
    }

    private void MonthlyRetentionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        MonthlyRetentionDaysText.IsEnabled = MonthlyRetentionCombo.SelectedIndex == 1;
    }

    private void ApplyConfiguration(BackupConfiguration config)
    {
        SourceDirectoryText.Text = config.SourceDirectory;
        PrimaryDestinationText.Text = config.PrimaryDestination;
        SecondaryDestinationText.Text = config.SecondaryDestination ?? string.Empty;
        EnableSecondaryDestinationCheck.IsChecked = config.SecondaryDestinationEnabled;
        BackupIntervalCombo.SelectedIndex = GetIntervalIndex(config.BackupIntervalMinutes);
        SnapshotFrequencyCombo.SelectedIndex = (int)config.SnapshotFrequency;
        IncrementalRetentionText.Text = config.RetentionPolicy.IncrementalRetentionDays.ToString();
        WeeklyRetentionText.Text = config.RetentionPolicy.WeeklySnapshotRetentionDays.ToString();
        StartWithWindowsCheck.IsChecked = config.StartWithWindows;

        if (config.RetentionPolicy.MonthlySnapshotRetentionDays is null)
        {
            MonthlyRetentionCombo.SelectedIndex = 0;
            MonthlyRetentionDaysText.Text = "365";
        }
        else
        {
            MonthlyRetentionCombo.SelectedIndex = 1;
            MonthlyRetentionDaysText.Text = config.RetentionPolicy.MonthlySnapshotRetentionDays.Value.ToString();
        }

        MonthlyRetentionDaysText.IsEnabled = MonthlyRetentionCombo.SelectedIndex == 1;
    }

    private BackupConfiguration BuildConfiguration()
    {
        var config = new BackupConfiguration
        {
            SourceDirectory = SourceDirectoryText.Text.Trim(),
            PrimaryDestination = PrimaryDestinationText.Text.Trim(),
            SecondaryDestination = SecondaryDestinationText.Text.Trim(),
            SecondaryDestinationEnabled = EnableSecondaryDestinationCheck.IsChecked == true,
            BackupIntervalMinutes = ParseMinutes(BackupIntervalCombo.Text),
            SnapshotFrequency = ParseSnapshotFrequency(SnapshotFrequencyCombo.Text),
            StartWithWindows = StartWithWindowsCheck.IsChecked == true,
            PasswordReference = PasswordStore.PasswordReference,
            RetentionPolicy = new RetentionPolicy
            {
                IncrementalRetentionDays = ParseInt(IncrementalRetentionText.Text, 7),
                WeeklySnapshotRetentionDays = ParseInt(WeeklyRetentionText.Text, 365),
                MonthlySnapshotRetentionDays = MonthlyRetentionCombo.SelectedIndex == 0
                    ? null
                    : ParseInt(MonthlyRetentionDaysText.Text, 365)
            }
        };

        if (config.SecondaryDestinationEnabled && string.IsNullOrWhiteSpace(config.SecondaryDestination))
        {
            throw new ArgumentException("Secondary destination is required when the secondary destination is enabled.");
        }

        return config;
    }

    private static int GetIntervalIndex(int minutes)
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

    private static int ParseMinutes(string value)
    {
        var trimmed = value.Replace(" minutes", string.Empty, StringComparison.OrdinalIgnoreCase);
        return int.TryParse(trimmed, out var minutes) ? minutes : 15;
    }

    private static SnapshotFrequency ParseSnapshotFrequency(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "daily" => SnapshotFrequency.Daily,
            "weekly" => SnapshotFrequency.Weekly,
            "monthly" => SnapshotFrequency.Monthly,
            _ => SnapshotFrequency.Weekly
        };
    }

    private static int ParseInt(string value, int fallback)
    {
        return int.TryParse(value, out var number) && number > 0 ? number : fallback;
    }

    private static string? PickDirectory()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Select a directory",
            ShowNewFolderButton = true
        };

        return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dialog.SelectedPath : null;
    }
}
