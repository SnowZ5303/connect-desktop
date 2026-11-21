using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ConnectDesktop.App.Services;

namespace ConnectDesktop.App;

public partial class App : Application
{
    /// <summary>Controlador único do app, criado em OnFrameworkInitializationCompleted.</summary>
    public static AppController? Controller { get; private set; }

    /// <summary>Página que a janela abre primeiro (argumento --page=nome). Padrão: connection.</summary>
    public static string StartPage { get; set; } = "connection";

    /// <summary>true quando o app foi aberto com --minimized: só a bandeja, sem janela ao iniciar.</summary>
    public static bool StartMinimized { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        ThemeService.Initialize();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var controller = new AppController();
            Controller = controller;

            // Idioma e tema antes de criar a janela, para os textos e cores já saírem certos.
            Loc.Instance.SetLanguage(controller.Settings.Language);
            ThemeService.Apply(controller.Settings.ThemeMode, controller.Settings.AccentColor);

            var window = new MainWindow(controller);
            if (!StartMinimized)
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
        var openItem = new NativeMenuItem(Loc.Instance["tray.open"]);
        openItem.Click += (_, _) => ShowWindow(window);

        var exitItem = new NativeMenuItem(Loc.Instance["tray.exit"]);
        exitItem.Click += (_, _) => _ = ExitAsync(desktop, window, controller);

        var menu = new NativeMenu();
        menu.Items.Add(openItem);
        menu.Items.Add(exitItem);

        var tray = new TrayIcon
        {
            Icon = AppIcon.Create(),
            ToolTipText = Loc.Instance["app.title"],
            Menu = menu,
            IsVisible = true
        };
        tray.Clicked += (_, _) => ShowWindow(window);

        // Os textos da bandeja acompanham a troca de idioma.
        Loc.Instance.LanguageChanged += () =>
        {
            openItem.Header = Loc.Instance["tray.open"];
            exitItem.Header = Loc.Instance["tray.exit"];
            tray.ToolTipText = Loc.Instance["app.title"];
        };

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
