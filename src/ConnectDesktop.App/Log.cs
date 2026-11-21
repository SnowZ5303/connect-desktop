using ConnectDesktop.Core.Settings;

namespace ConnectDesktop.App;

/// <summary>Log simples em arquivo (AppPaths.LogFile). Thread-safe; nunca lança.</summary>
internal static class Log
{
    private const long MaxBytes = 2L * 1024 * 1024;
    private static readonly object Gate = new();

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null)
        => Write("ERRO", ex is null ? message : $"{message} {ex}");

    private static void Write(string level, string text)
    {
        try
        {
            lock (Gate)
            {
                var path = AppPaths.LogFile;
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                {
                    var old = path + ".old";
                    if (File.Exists(old))
                        File.Delete(old);
                    File.Move(path, old);
                }

                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {text}{Environment.NewLine}");
            }
        }
        catch
        {
            // Log nunca pode derrubar o app.
        }
    }
}
