#nullable enable
using System;
using System.Runtime.InteropServices;

namespace ConnectDesktop.Platform.Linux;

/// <summary>
/// Assinaturas P/Invoke do Xlib (libX11.so.6) e do XTest (libXtst.so.6).
/// Tipos x86_64 LP64: Window, Time, Cursor e unsigned long = nuint; int/unsigned int = 32 bits; Bool/Status = int.
/// </summary>
internal static class XNative
{
    private const string X11 = "libX11.so.6";
    private const string Xtst = "libXtst.so.6";

    [DllImport(X11)] internal static extern int XInitThreads();

    [DllImport(X11)] internal static extern IntPtr XOpenDisplay(string? displayName);

    [DllImport(X11)] internal static extern int XCloseDisplay(IntPtr display);

    [DllImport(X11)] internal static extern int XDefaultScreen(IntPtr display);

    [DllImport(X11)] internal static extern nuint XRootWindow(IntPtr display, int screenNumber);

    [DllImport(X11)] internal static extern int XDisplayWidth(IntPtr display, int screenNumber);

    [DllImport(X11)] internal static extern int XDisplayHeight(IntPtr display, int screenNumber);

    [DllImport(X11)]
    internal static extern int XQueryPointer(
        IntPtr display, nuint window,
        out nuint rootReturn, out nuint childReturn,
        out int rootX, out int rootY,
        out int winX, out int winY,
        out uint maskReturn);

    [DllImport(X11)]
    internal static extern int XWarpPointer(
        IntPtr display, nuint srcWindow, nuint destWindow,
        int srcX, int srcY, uint srcWidth, uint srcHeight,
        int destX, int destY);

    [DllImport(X11)] internal static extern int XFlush(IntPtr display);

    [DllImport(X11)] internal static extern int XPending(IntPtr display);

    [DllImport(X11)] internal static extern int XNextEvent(IntPtr display, byte[] eventReturn);

    [DllImport(X11)] internal static extern int XPeekEvent(IntPtr display, byte[] eventReturn);

    [DllImport(X11)]
    internal static extern int XGrabPointer(
        IntPtr display, nuint grabWindow, int ownerEvents, uint eventMask,
        int pointerMode, int keyboardMode, nuint confineTo, nuint cursor, nuint time);

    [DllImport(X11)] internal static extern int XUngrabPointer(IntPtr display, nuint time);

    [DllImport(X11)]
    internal static extern int XGrabKeyboard(
        IntPtr display, nuint grabWindow, int ownerEvents,
        int pointerMode, int keyboardMode, nuint time);

    [DllImport(X11)] internal static extern int XUngrabKeyboard(IntPtr display, nuint time);

    [DllImport(Xtst)]
    internal static extern int XTestFakeMotionEvent(IntPtr display, int screenNumber, int x, int y, nuint delay);

    [DllImport(Xtst)]
    internal static extern int XTestFakeButtonEvent(IntPtr display, uint button, int isPress, nuint delay);

    [DllImport(Xtst)]
    internal static extern int XTestFakeKeyEvent(IntPtr display, uint keycode, int isPress, nuint delay);
}
