using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SukiUI;
using Avalonia.Styling;
using ConfigStore = EVA.Infrastructure.ConfigStore;
using EVA.Infrastructure;
using EVA.Core;
using EVA.Core.Models;

namespace EVA.App;

public partial class App : Application
{
    private static readonly string AppDataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EVA");
    public static ConfigStore ConfigStore { get; } = new(AppDataDirectory);
    public static PasswordStore PasswordStore { get; } = new();
    public static BackupOrchestrator? Orchestrator { get; private set; }
    public static CancellationTokenSource? OrchestratorCancellation { get; private set; }

    private TrayIcon? _trayIcon;
    private SukiTheme? _theme;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            _theme = SukiTheme.GetInstance();
            _theme.ChangeBaseTheme(ThemeVariant.Dark);

            CleanUpStaleTemporaryFiles();
            CreateTrayIcon();

            if (IsFirstRun())
            {
                var optionsWindow = new OptionsWindow();
                optionsWindow.Show();
            }
            else
            {
                _ = InitializeApplicationAsync();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static bool IsFirstRun()
    {
        var configExists = ConfigStore.ExistsAsync().GetAwaiter().GetResult();
        return !configExists || !PasswordStore.HasStoredPassword();
    }

    public static async Task InitializeApplicationAsync()
    {
        try
        {
            var config = await ConfigStore.LoadAsync();
            if (TryValidateConfig(config, out _))
            {
                await StartOrchestratorAsync(config);
                return;
            }

            var options = new OptionsWindow();
            options.Show();
        }
        catch (Exception ex)
        {
            var options = new OptionsWindow();
            options.Show();
            System.Diagnostics.Debug.WriteLine($"EVA startup error: {ex.Message}");
        }
    }

    public static async Task StartOrchestratorAsync(BackupConfiguration config)
    {
        var password = PasswordStore.GetPassword();
        if (string.IsNullOrWhiteSpace(password))
        {
            var options = new OptionsWindow();
            options.Show();
            return;
        }

        OrchestratorCancellation?.Cancel();
        OrchestratorCancellation = new CancellationTokenSource();

        var orchestrator = new BackupOrchestrator(
            config.SourceDirectory,
            config.PrimaryDestination,
            config.SecondaryDestinationEnabled ? config.SecondaryDestination : null,
            password,
            sourceId: "eva-app");

        Orchestrator = orchestrator;

        _ = Task.Run(() => orchestrator.StartAsync(
            TimeSpan.FromMinutes(config.BackupIntervalMinutes),
            OrchestratorCancellation.Token));
    }

    public static async Task RefreshOrchestratorAsync(BackupConfiguration config)
    {
        if (TryValidateConfig(config, out _))
        {
            await StartOrchestratorAsync(config);
            return;
        }

        OrchestratorCancellation?.Cancel();
        Orchestrator = null;
    }

    public static bool TryValidateConfig(BackupConfiguration config, out string? error)
    {
        error = null;
        try
        {
            var validator = new ConfigValidator(PasswordStore);
            validator.Validate(config);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static void CleanUpStaleTemporaryFiles()
    {
        var tempRoots = new[]
        {
            Path.GetTempPath(),
            Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData), "EVA", "Temp")
        };

        foreach (var root in tempRoots.Distinct())
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    if (Path.GetExtension(file).Equals(".tmp", StringComparison.OrdinalIgnoreCase))
                    {
                        try { File.Delete(file); } catch { }
                    }
                }
            }
            catch { }
        }
    }

    private void CreateTrayIcon()
    {
        var menu = new NativeMenu();

        var titleItem = new NativeMenuItem { Header = "EVA" };
        titleItem.IsEnabled = false;
        menu.Add(titleItem);

        menu.Add(new NativeMenuItemSeparator());

        var snapshotItem = new NativeMenuItem { Header = "Create snapshot now" };
        snapshotItem.Click += (_, _) => _ = CreateSnapshotNowAsync();
        menu.Add(snapshotItem);

        var listItem = new NativeMenuItem { Header = "List snapshot content" };
        listItem.Click += (_, _) => ShowListSnapshots();
        menu.Add(listItem);

        var restoreItem = new NativeMenuItem { Header = "Restore snapshot" };
        restoreItem.Click += (_, _) => ShowRestoreSnapshot();
        menu.Add(restoreItem);

        menu.Add(new NativeMenuItemSeparator());

        var optionsItem = new NativeMenuItem { Header = "Options" };
        optionsItem.Click += (_, _) => ShowOptions();
        menu.Add(optionsItem);

        menu.Add(new NativeMenuItemSeparator());

        var exitItem = new NativeMenuItem { Header = "Exit" };
        exitItem.Click += (_, _) => Shutdown();
        menu.Add(exitItem);

        _trayIcon = new TrayIcon
        {
            ToolTipText = "EVA",
            Menu = menu,
            IsVisible = true
        };
    }

    private void ShowOptions()
    {
        var optionsWindow = new OptionsWindow();
        optionsWindow.Show();
    }

    private void Shutdown()
    {
        _trayIcon?.Dispose();
        OrchestratorCancellation?.Cancel();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    private static async Task CreateSnapshotNowAsync()
    {
        if (Orchestrator is null)
        {
            System.Diagnostics.Debug.WriteLine("Cannot create snapshot: orchestrator not running.");
            return;
        }

        try
        {
            await Orchestrator.RunOnceAsync(manualSnapshot: true, cancellationToken: CancellationToken.None);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Manual snapshot failed: {ex.Message}");
        }
    }

    private static void ShowListSnapshots()
    {
        // TODO: implement ListSnapshotWindow
    }

    private static void ShowRestoreSnapshot()
    {
        // TODO: implement RestoreSnapshotWindow
    }
}