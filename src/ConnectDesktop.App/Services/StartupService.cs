// Dono: g11. Assinaturas públicas FIXAS (escritas pelo main).
using System.Runtime.Versioning;
using System.Security;
using Microsoft.Win32;

namespace ConnectDesktop.App.Services;

/// <summary>
/// Abrir com o sistema.
/// - Windows: HKCU\Software\Microsoft\Windows\CurrentVersion\Run, valor "ConnectDesktop" = "\"&lt;exe&gt;\"" (+ " --minimized").
/// - macOS: ~/Library/LaunchAgents/com.connectdesktop.app.plist (ProgramArguments com o executável do bundle, RunAtLoad true).
/// - Linux: ~/.config/autostart/connect-desktop.desktop (Exec com o caminho do executável).
/// Caminho do executável: Environment.ProcessPath.
/// Nunca lança: erros vão para Log.Error e o método retorna false.
/// </summary>
public static class StartupService
{
    /// <summary>Argumento de linha de comando que faz o app abrir só na bandeja.</summary>
    public const string MinimizedArg = "--minimized";

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "ConnectDesktop";
    private const string LaunchAgentLabel = "com.connectdesktop.app";
    private const string LinuxDesktopFileName = "connect-desktop.desktop";

    public static bool IsEnabled()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return IsEnabledWindows();
            if (OperatingSystem.IsMacOS())
                return File.Exists(MacPlistPath());
            if (OperatingSystem.IsLinux())
                return File.Exists(LinuxDesktopPath());

            return false;
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao consultar abertura com o sistema.", ex);
            return false;
        }
    }

    /// <summary>Liga/desliga. Se ligado, <paramref name="minimized"/> acrescenta <see cref="MinimizedArg"/>.</summary>
    public static bool SetEnabled(bool enabled, bool minimized)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
            {
                Log.Error("Caminho do executável indisponível; abertura com o sistema não alterada.");
                return false;
            }

            if (OperatingSystem.IsWindows())
                return SetEnabledWindows(enabled, minimized, exe);
            if (OperatingSystem.IsMacOS())
                return SetEnabledMac(enabled, minimized, exe);
            if (OperatingSystem.IsLinux())
                return SetEnabledLinux(enabled, minimized, exe);

            Log.Info("Abertura com o sistema não suportada nesta plataforma.");
            return false;
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao alterar abertura com o sistema.", ex);
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool IsEnabledWindows()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(RunValueName) is string value && !string.IsNullOrWhiteSpace(value);
    }

    [SupportedOSPlatform("windows")]
    private static bool SetEnabledWindows(bool enabled, bool minimized, string exe)
    {
        if (enabled)
        {
            var data = "\"" + exe + "\"" + (minimized ? " " + MinimizedArg : string.Empty);
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            key.SetValue(RunValueName, data, RegistryValueKind.String);
            Log.Info("Abertura com o Windows ligada.");
        }
        else
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(RunValueName, throwOnMissingValue: false);
            Log.Info("Abertura com o Windows desligada.");
        }

        return true;
    }

    private static string MacPlistPath()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", LaunchAgentLabel + ".plist");

    private static bool SetEnabledMac(bool enabled, bool minimized, string exe)
    {
        var path = MacPlistPath();
        if (enabled)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var args = "    <string>" + SecurityElement.Escape(exe) + "</string>\n";
            if (minimized)
                args += "    <string>" + MinimizedArg + "</string>\n";

            var plist =
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
                "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
                "<plist version=\"1.0\">\n" +
                "<dict>\n" +
                "  <key>Label</key>\n" +
                "  <string>" + LaunchAgentLabel + "</string>\n" +
                "  <key>ProgramArguments</key>\n" +
                "  <array>\n" +
                args +
                "  </array>\n" +
                "  <key>RunAtLoad</key>\n" +
                "  <true/>\n" +
                "</dict>\n" +
                "</plist>\n";

            File.WriteAllText(path, plist);
            Log.Info("Abertura com o macOS ligada.");
        }
        else
        {
            if (File.Exists(path))
                File.Delete(path);
            Log.Info("Abertura com o macOS desligada.");
        }

        return true;
    }

    private static string LinuxDesktopPath()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "autostart", LinuxDesktopFileName);

    private static bool SetEnabledLinux(bool enabled, bool minimized, string exe)
    {
        var path = LinuxDesktopPath();
        if (enabled)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var execLine = "Exec=\"" + exe + "\"" + (minimized ? " " + MinimizedArg : string.Empty);

            var desktop =
                "[Desktop Entry]\n" +
                "Type=Application\n" +
                "Name=Connect Desktop\n" +
                execLine + "\n" +
                "X-GNOME-Autostart-enabled=true\n";

            File.WriteAllText(path, desktop);
            Log.Info("Abertura com o Linux ligada.");
        }
        else
        {
            if (File.Exists(path))
                File.Delete(path);
            Log.Info("Abertura com o Linux desligada.");
        }

        return true;
    }
}
