#nullable enable
using System;
using ConnectDesktop.Core.Platform;

namespace ConnectDesktop.Platform.Linux;

/// <summary>Informações de tela e cursor via Xlib. Cada chamada usa o seu próprio Display e fecha ao final.</summary>
internal sealed class LinuxScreenInfo : IScreenInfo
{
    public ScreenBounds GetDesktopBounds()
    {
        using XDisplay display = OpenDisplay();
        int screen = XNative.XDefaultScreen(display.Handle);
        int width = XNative.XDisplayWidth(display.Handle, screen);
        int height = XNative.XDisplayHeight(display.Handle, screen);
        return new ScreenBounds(0, 0, width, height);
    }

    public (int X, int Y) GetCursorPosition()
    {
        using XDisplay display = OpenDisplay();
        XNative.XQueryPointer(display.Handle, display.Root,
            out _, out _, out int rootX, out int rootY, out _, out _, out _);
        return (rootX, rootY);
    }

    public void SetCursorPosition(int x, int y)
    {
        using XDisplay display = OpenDisplay();
        XNative.XWarpPointer(display.Handle, 0, display.Root, 0, 0, 0, 0, x, y);
        XNative.XFlush(display.Handle);
    }

    private static XDisplay OpenDisplay()
    {
        return XDisplay.TryOpen() ?? throw new InvalidOperationException(XDisplay.OpenErrorMessage);
    }
}
