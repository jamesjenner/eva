using System.IO;
using System.Windows;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using EVA.Core;
using EVA.Core.Models;
using EVA.Infrastructure;

namespace EVA.App;

public partial class App : Application
{
    private static readonly string AppDataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EVA");
    public static ConfigStore ConfigStore { get; } = new(AppDataDirectory);
    public static BackupOrchestrator? Orchestrator { get; private set; }
    public static CancellationTokenSource? OrchestratorCancellation { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var shell = new ShellWindow();
        MainWindow = shell;
        shell.Show();
        shell.WindowState = WindowState.Minimized;
        shell.ShowInTaskbar = false;
        shell.Hide();

        CleanUpStaleTemporaryFiles();

        _ = InitializeApplicationAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (OrchestratorCancellation is not null && !OrchestratorCancellation.IsCancellationRequested)
        {
            OrchestratorCancellation.Cancel();
        }

        Orchestrator?.StopAsync().GetAwaiter().GetResult();

        base.OnExit(e);
    }

    public static async Task InitializeApplicationAsync()
    {
        try
        {
            var config = await ConfigStore.LoadAsync();
            if (TryValidateConfig(config, out var validationError))
            {
                await StartOrchestratorAsync(config);
                return;
            }

            var options = new OptionsWindow();
            options.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"EVA could not start: {ex.Message}", "EVA", MessageBoxButton.OK, MessageBoxImage.Warning);
            var fallback = new OptionsWindow();
            fallback.Show();
        }
    }

    public static async Task StartOrchestratorAsync(BackupConfiguration config)
    {
        var password = PasswordStore.GetPassword(config.PasswordReference);
        if (string.IsNullOrWhiteSpace(password))
        {
            var options = new OptionsWindow();
            options.Show();
            return;
        }

        OrchestratorCancellation?.Cancel();
        OrchestratorCancellation = new CancellationTokenSource();

        Orchestrator = new BackupOrchestrator(
            config.SourceDirectory,
            config.PrimaryDestination,
            config.SecondaryDestinationEnabled ? config.SecondaryDestination : null,
            password,
            sourceId: "eva-app");

        _ = Task.Run(() => Orchestrator.StartAsync(TimeSpan.FromMinutes(config.BackupIntervalMinutes), OrchestratorCancellation.Token));
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
            var validator = new ConfigValidator();
            validator.Validate(config);
            if (string.IsNullOrWhiteSpace(config.PasswordReference) || PasswordStore.GetPassword(config.PasswordReference) is null)
            {
                error = "A valid encryption password is required.";
                return false;
            }

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
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EVA", "Temp")
        };

        foreach (var root in tempRoots.Distinct())
        {
            try
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }

                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    if (Path.GetExtension(file).Equals(".tmp", StringComparison.OrdinalIgnoreCase)
                        || file.Contains(".eva-write-test-", StringComparison.OrdinalIgnoreCase)
                        || file.Contains("_restore_", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            File.Delete(file);
                        }
                        catch
                        {
                            // Ignore stale cleanup failures in the temp area.
                        }
                    }
                }
            }
            catch
            {
                // Ignore stale cleanup failures.
            }
        }
    }
}
