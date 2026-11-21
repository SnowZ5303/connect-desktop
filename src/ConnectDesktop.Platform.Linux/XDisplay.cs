#nullable enable
using System;

namespace ConnectDesktop.Platform.Linux;

/// <summary>
/// Conexão com o X server (Display*). Cada componente (captura, injetor, tela) abre o seu.
/// XInitThreads() é chamado uma única vez, antes da primeira abertura.
/// </summary>
internal sealed class XDisplay : IDisposable
{
    internal const string OpenErrorMessage =
        "Não foi possível abrir o display X11. O Connect Desktop precisa de uma sessão X11 (Xorg).";

    private static readonly object InitLock = new();
    private static bool _threadsInitialized;

    private IntPtr _handle;

    private XDisplay(IntPtr handle)
    {
        _handle = handle;
        Screen = XNative.XDefaultScreen(handle);
        Root = XNative.XRootWindow(handle, Screen);
    }

    internal IntPtr Handle => _handle;

    /// <summary>Número da tela padrão.</summary>
    internal int Screen { get; }

    /// <summary>Janela raiz da tela padrão.</summary>
    internal nuint Root { get; }

    /// <summary>Abre uma conexão com o display padrão. Retorna null se não for possível abrir.</summary>
    internal static XDisplay? TryOpen()
    {
        try
        {
            EnsureThreads();
            IntPtr handle = XNative.XOpenDisplay(null);
            if (handle == IntPtr.Zero) return null;

            try
            {
                return new XDisplay(handle);
            }
            catch (Exception)
            {
                XNative.XCloseDisplay(handle);
                return null;
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void EnsureThreads()
    {
        lock (InitLock)
        {
            if (_threadsInitialized) return;
            XNative.XInitThreads();
            _threadsInitialized = true;
        }
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        XNative.XCloseDisplay(_handle);
        _handle = IntPtr.Zero;
    }
}
