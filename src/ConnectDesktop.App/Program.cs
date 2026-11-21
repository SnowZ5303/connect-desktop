using Avalonia;

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

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
