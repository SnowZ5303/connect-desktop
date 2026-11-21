using Avalonia;
using ConnectDesktop.App.Services;

namespace ConnectDesktop.App;

internal static class Program
{
    private const string SingleInstanceName = "ConnectDesktop-SingleInstance";

    [STAThread]
    public static void Main(string[] args)
    {
        // "Global\" só no Windows (sessões diferentes); Mac/Linux usam o nome simples.
        var mutexName = OperatingSystem.IsWindows() ? $"Global\\{SingleInstanceName}" : SingleInstanceName;
        var mutex = new Mutex(true, mutexName, out bool created);
        if (!created)
        {
            // Já existe uma instância rodando: esta sai sem ativar a outra.
            mutex.Dispose();
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error("Erro não tratado.", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Erro em tarefa em segundo plano.", e.Exception);
            e.SetObserved();
        };

        try
        {
            App.StartMinimized = Array.IndexOf(args, StartupService.MinimizedArg) >= 0;
            App.StartPage = ParsePage(args);
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao iniciar o aplicativo.", ex);
        }
        finally
        {
            mutex?.Dispose();
        }
    }

    /// <summary>Lê --page=nome (teste de telas). Nome desconhecido ou ausente: connection.</summary>
    private static string ParsePage(string[] args)
    {
        string[] pages = { "connection", "devices", "transfers", "pairs", "general", "appearance", "language", "startup", "about" };
        const string prefix = "--page=";
        foreach (var arg in args)
        {
            if (!arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            var name = arg[prefix.Length..].Trim().ToLowerInvariant();
            if (Array.IndexOf(pages, name) >= 0)
                return name;
        }
        return "connection";
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
