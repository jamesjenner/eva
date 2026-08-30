using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SukiUI;
using SukiUI.Enums;
using Avalonia.Styling;

namespace EVA.App;

public partial class App : Application
{
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

            // var mainWindow = new MainWindow
            // {
            //     ShowInTaskbar = false,
            //     WindowStartupLocation = WindowStartupLocation.CenterScreen,
            //     CanResize = false,
            //     IsVisible = false
            // };

            // desktop.MainWindow = mainWindow;
            CreateTrayIcon();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void CreateTrayIcon()
    {
        var menu = new NativeMenu();

        var titleItem = new NativeMenuItem { Header = "EVA" };
        titleItem.IsEnabled = false;
        menu.Add(titleItem);

        menu.Add(new NativeMenuItemSeparator());

        menu.Add(new NativeMenuItem { Header = "Create snapshot now" });
        menu.Add(new NativeMenuItem { Header = "List snapshot content" });
        menu.Add(new NativeMenuItem { Header = "Restore snapshot" });

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
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }
}