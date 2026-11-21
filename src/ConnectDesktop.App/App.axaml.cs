using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace ConnectDesktop.App;

public partial class App : Application
{
    /// <summary>Controlador único do app, criado em OnFrameworkInitializationCompleted.</summary>
    public static AppController? Controller { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var controller = new AppController();
            Controller = controller;
            var window = new MainWindow(controller);
            desktop.MainWindow = window;

            controller.OwnerWindow = window;
            controller.RequestShutdown = () => Quit(desktop, window);

            CreateTrayIcon(desktop, window, controller);
            controller.Start();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void CreateTrayIcon(IClassicDesktopStyleApplicationLifetime desktop, MainWindow window, AppController controller)
    {
        var openItem = new NativeMenuItem("Abrir");
        openItem.Click += (_, _) => ShowWindow(window);

        var exitItem = new NativeMenuItem("Sair");
        exitItem.Click += (_, _) => _ = ExitAsync(desktop, window, controller);

        var menu = new NativeMenu();
        menu.Items.Add(openItem);
        menu.Items.Add(exitItem);

        var tray = new TrayIcon
        {
            Icon = AppIcon.Create(),
            ToolTipText = "Connect Desktop",
            Menu = menu,
            IsVisible = true
        };
        tray.Clicked += (_, _) => ShowWindow(window);

        TrayIcon.SetIcons(this, new TrayIcons { tray });
    }

    private static void ShowWindow(MainWindow window)
    {
        window.Show();
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private static async Task ExitAsync(IClassicDesktopStyleApplicationLifetime desktop, MainWindow window, AppController controller)
    {
        await controller.ShutdownAsync();
        Quit(desktop, window);
    }

    private static void Quit(IClassicDesktopStyleApplicationLifetime desktop, MainWindow window)
    {
        window.AllowClose();
        desktop.Shutdown();
    }
}
