using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
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
    public static string PreviewCacheRoot { get; } = Path.Combine(AppDataDirectory, "preview-cache");
    public static ConfigStore ConfigStore { get; } = new(AppDataDirectory);
    public static PasswordStore PasswordStore { get; } = new();
    public static BackupOrchestrator? Orchestrator { get; private set; }
    public static CancellationTokenSource? OrchestratorCancellation { get; private set; }
    public static BackupStatusWindow? StatusWindow { get; internal set; }

    private static App? _currentApp;
    private ListSnapshotWindow? _listSnapshotWindow;
    private RestoreWindow? _restoreWindow;
    private OptionsWindow? _optionsWindow;
    private static TrayIcon? _trayIcon;
    private SukiTheme? _theme;
    private static TimeSpan _orchestratorInterval = TimeSpan.FromMinutes(15);

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        _currentApp = this;
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            _theme = SukiTheme.GetInstance();
            _theme.ChangeBaseTheme(ThemeVariant.Dark);

            CleanUpStaleTemporaryFiles();
            CreateTrayIcon();

            if (IsFirstRun())
            {
                ShowOptions();
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

            _currentApp?.ShowOptions();
        }
        catch
        {
            _currentApp?.ShowOptions();
        }
    }

    public static async Task StartOrchestratorAsync(BackupConfiguration config)
    {
        var password = PasswordStore.GetPassword();
        if (string.IsNullOrWhiteSpace(password))
        {
            _currentApp?.ShowOptions();
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
        _orchestratorInterval = TimeSpan.FromMinutes(config.BackupIntervalMinutes);

        _ = Task.Run(() => orchestrator.StartAsync(
            _orchestratorInterval,
            OrchestratorCancellation.Token,
            result => HandleBackupResultAsync(result, _orchestratorInterval)));
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
        try
        {
            if (Directory.Exists(PreviewCacheRoot))
            {
                Directory.Delete(PreviewCacheRoot, recursive: true);
            }
        }
        catch
        {
        }

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

        var snapshotItem = new NativeMenuItem { Header = "Create full backup now" };
        snapshotItem.Click += (_, _) => _ = CreateSnapshotNowAsync();
        menu.Add(snapshotItem);

        var listItem = new NativeMenuItem { Header = "List full backup contents" };
        listItem.Click += (_, _) => ShowListSnapshots();
        menu.Add(listItem);

        var restoreItem = new NativeMenuItem { Header = "Restore full backup" };
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
        Dispatcher.UIThread.VerifyAccess();
        if (ActivateIfOpen(_optionsWindow))
        {
            return;
        }

        var window = new OptionsWindow();
        _optionsWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_optionsWindow, window))
            {
                _optionsWindow = null;
            }
        };
        window.Show();
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

    public static async Task CreateSnapshotNowAsync()
    {
        if (Orchestrator is null)
        {
            return;
        }

        try
        {
            var result = await Orchestrator.RunOnceAsync(manualSnapshot: true, cancellationToken: CancellationToken.None);
            await HandleBackupResultAsync(result, _orchestratorInterval);
        }
        catch
        {
        }
    }

    private void ShowListSnapshots()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (ActivateIfOpen(_listSnapshotWindow))
        {
            return;
        }

        var window = new ListSnapshotWindow();
        _listSnapshotWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_listSnapshotWindow, window))
            {
                _listSnapshotWindow = null;
            }
        };
        window.Show();
    }

    private void ShowRestoreSnapshot()
    {
        ShowListSnapshots();
    }

    internal static void ShowRestoreSnapshot(string archivePath)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_currentApp is not { } app)
        {
            return;
        }

        app.ShowRestoreWindow(archivePath);
    }

    private void ShowRestoreWindow(string archivePath)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (ActivateIfOpen(_restoreWindow))
        {
            return;
        }

        var password = PasswordStore.GetPassword();
        if (string.IsNullOrWhiteSpace(password))
        {
            return;
        }

        var window = new RestoreWindow(archivePath, password);
        _restoreWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_restoreWindow, window))
            {
                _restoreWindow = null;
            }
        };
        window.Show();
    }

    private static bool ActivateIfOpen(Window? window)
    {
        if (window is null)
        {
            return false;
        }

        window.WindowState = WindowState.Normal;
        window.Activate();
        return true;
    }

    private static Task HandleBackupResultAsync(BackupRunResult result, TimeSpan interval)
    {
        return Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (result.UnstableFiles.Count > 0)
            {
                var paths = result.UnstableFiles.Select(file => file.RelativePath);
                var nextRun = DateTimeOffset.UtcNow + interval;
                ShowOrUpdateStatusWindow(paths, result.LastSuccessfulBackupUtc, nextRun);
            }
            else
            {
                CloseStatusWindowIfOpen();
            }
        }).GetTask();
    }

    private static void ShowOrUpdateStatusWindow(
        IEnumerable<string> unstableFilePaths,
        DateTimeOffset? lastSuccessfulBackup,
        DateTimeOffset nextScheduledRun)
    {
        if (StatusWindow is not null)
        {
            StatusWindow.Update(unstableFilePaths, lastSuccessfulBackup, nextScheduledRun);
        }
        else
        {
            StatusWindow = new BackupStatusWindow(unstableFilePaths, lastSuccessfulBackup, nextScheduledRun);
            StatusWindow.Closed += (_, _) => StatusWindow = null;
            StatusWindow.Show();
        }
    }

    private static void CloseStatusWindowIfOpen()
    {
        StatusWindow?.Close();
        StatusWindow = null;
    }

}