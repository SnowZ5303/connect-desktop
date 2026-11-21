#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using ConnectDesktop.Core.Platform;

namespace ConnectDesktop.Platform.Linux;

/// <summary>Plataforma Linux (X11). Só X11 (Xorg); Wayland puro não é suportado.</summary>
public sealed class LinuxPlatform : IPlatform
{
    private static readonly string[] ClipboardToolPaths =
    {
        "/usr/bin/xclip",
        "/usr/local/bin/xclip",
        "/usr/bin/xsel",
        "/usr/local/bin/xsel",
    };

    private readonly LinuxScreenInfo _screen = new();
    private readonly LinuxClipboard _clipboard = new();

    public string Name => "Linux (X11)";

    public IScreenInfo Screen => _screen;

    public IClipboard Clipboard => _clipboard;

    public IInputCapture CreateCapture() => new LinuxInputCapture();

    public IInputInjector CreateInjector() => new LinuxInputInjector();

    public bool CheckPermissions(out string message)
    {
        message = string.Empty;

        string? display = Environment.GetEnvironmentVariable("DISPLAY");
        if (string.IsNullOrEmpty(display))
        {
            message = "Nenhuma sessão X11 encontrada. Entre numa sessão 'Xorg' (no Ubuntu: engrenagem na tela de login > Ubuntu on Xorg).";
            return false;
        }

        string? sessionType = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");
        if (string.Equals(sessionType, "wayland", StringComparison.OrdinalIgnoreCase))
        {
            message = "Sessão Wayland detectada: o Linux bloqueia o controle de mouse/teclado por outros apps. Entre numa sessão Xorg.";
            return false;
        }

        try
        {
            XDisplay? x = XDisplay.TryOpen();
            if (x == null)
            {
                message = XDisplay.OpenErrorMessage;
                return false;
            }
            x.Dispose();
        }
        catch (Exception)
        {
            message = XDisplay.OpenErrorMessage;
            return false;
        }

        try
        {
            if (!NativeLibrary.TryLoad("libXtst.so.6", out IntPtr library))
            {
                message = "Instale o pacote libxtst6.";
                return false;
            }
            NativeLibrary.Free(library);
        }
        catch (Exception)
        {
            message = "Instale o pacote libxtst6.";
            return false;
        }

        bool hasClipboardTool = ClipboardToolPaths.Any(File.Exists);
        if (!hasClipboardTool)
        {
            message = "Para compartilhar a área de transferência, instale o xclip.";
        }

        return true;
    }
}
