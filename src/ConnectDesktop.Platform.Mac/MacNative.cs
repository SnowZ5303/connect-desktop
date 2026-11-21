// Dono: g04. Declarações P/Invoke do macOS (ApplicationServices, CoreFoundation, CoreGraphics).
using System.Runtime.InteropServices;

namespace ConnectDesktop.Platform.Mac;

/// <summary>Ponto em coordenadas globais (CGFloat = double em 64 bits).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CGPoint
{
    public double X;
    public double Y;

    public CGPoint(double x, double y)
    {
        X = x;
        Y = y;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct CGSize
{
    public double Width;
    public double Height;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CGRect
{
    public CGPoint Origin;
    public CGSize Size;
}

/// <summary>Constantes de CGEventType.</summary>
internal static class CGEventTypes
{
    public const uint LeftMouseDown = 1;
    public const uint LeftMouseUp = 2;
    public const uint RightMouseDown = 3;
    public const uint RightMouseUp = 4;
    public const uint MouseMoved = 5;
    public const uint LeftMouseDragged = 6;
    public const uint RightMouseDragged = 7;
    public const uint KeyDown = 10;
    public const uint KeyUp = 11;
    public const uint FlagsChanged = 12;
    public const uint ScrollWheel = 22;
    public const uint OtherMouseDown = 25;
    public const uint OtherMouseUp = 26;
    public const uint OtherMouseDragged = 27;
    public const uint TapDisabledByTimeout = 0xFFFFFFFE;
    public const uint TapDisabledByUserInput = 0xFFFFFFFF;
}

/// <summary>Campos de CGEventField usados pelo projeto.</summary>
internal static class CGEventFields
{
    public const uint MouseEventClickState = 1;
    public const uint MouseEventButtonNumber = 3;
    public const uint MouseEventDeltaX = 4;
    public const uint MouseEventDeltaY = 5;
    public const uint KeyboardEventKeycode = 9;
    public const uint ScrollWheelEventDeltaAxis1 = 11;
    public const uint ScrollWheelEventDeltaAxis2 = 12;
}

/// <summary>Máscaras de CGEventFlags usadas para detectar modificadores.</summary>
internal static class CGEventFlagMasks
{
    public const ulong AlphaShift = 0x00010000UL;
    public const ulong Shift = 0x00020000UL;
    public const ulong Control = 0x00040000UL;
    public const ulong Alternate = 0x00080000UL;
    public const ulong Command = 0x00100000UL;
}

internal static class MacNative
{
    internal const string ApplicationServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    internal const string HIServices = "/System/Library/Frameworks/ApplicationServices.framework/Frameworks/HIServices.framework/HIServices";
    internal const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    internal const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

    /// <summary>Callback do CGEventTap: CGEventRef (*)(CGEventTapProxy, CGEventType, CGEventRef, void*).</summary>
    internal delegate IntPtr CGEventTapCallBack(IntPtr proxy, uint type, IntPtr evt, IntPtr refcon);

    // ---------- ApplicationServices (Acessibilidade) ----------

    [DllImport(ApplicationServices)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool AXIsProcessTrusted();

    [DllImport(ApplicationServices)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool AXIsProcessTrustedWithOptions(IntPtr options);

    // ---------- CoreFoundation ----------

    [DllImport(CoreFoundation)]
    internal static extern void CFRelease(IntPtr cf);

    [DllImport(CoreFoundation)]
    internal static extern IntPtr CFMachPortCreateRunLoopSource(IntPtr allocator, IntPtr port, long order);

    [DllImport(CoreFoundation)]
    internal static extern void CFMachPortInvalidate(IntPtr port);

    [DllImport(CoreFoundation)]
    internal static extern IntPtr CFRunLoopGetCurrent();

    [DllImport(CoreFoundation)]
    internal static extern void CFRunLoopAddSource(IntPtr rl, IntPtr source, IntPtr mode);

    [DllImport(CoreFoundation)]
    internal static extern void CFRunLoopRun();

    [DllImport(CoreFoundation)]
    internal static extern void CFRunLoopStop(IntPtr rl);

    [DllImport(CoreFoundation)]
    internal static extern IntPtr CFDictionaryCreate(IntPtr allocator, IntPtr[] keys, IntPtr[] values, long numValues, IntPtr keyCallBacks, IntPtr valueCallBacks);

    // ---------- CoreGraphics: telas ----------

    [DllImport(CoreGraphics)]
    internal static extern uint CGMainDisplayID();

    [DllImport(CoreGraphics)]
    internal static extern int CGGetActiveDisplayList(uint maxDisplays, uint[] activeDisplays, out uint displayCount);

    [DllImport(CoreGraphics)]
    internal static extern CGRect CGDisplayBounds(uint display);

    [DllImport(CoreGraphics)]
    internal static extern int CGWarpMouseCursorPosition(CGPoint newCursorPosition);

    [DllImport(CoreGraphics)]
    internal static extern int CGAssociateMouseAndMouseCursorPosition([MarshalAs(UnmanagedType.I1)] bool connected);

    // ---------- CoreGraphics: eventos ----------

    [DllImport(CoreGraphics)]
    internal static extern IntPtr CGEventCreate(IntPtr source);

    [DllImport(CoreGraphics)]
    internal static extern CGPoint CGEventGetLocation(IntPtr evt);

    [DllImport(CoreGraphics)]
    internal static extern ulong CGEventGetFlags(IntPtr evt);

    [DllImport(CoreGraphics)]
    internal static extern long CGEventGetIntegerValueField(IntPtr evt, uint field);

    [DllImport(CoreGraphics)]
    internal static extern void CGEventSetIntegerValueField(IntPtr evt, uint field, long value);

    [DllImport(CoreGraphics)]
    internal static extern IntPtr CGEventSourceCreate(uint stateID);

    [DllImport(CoreGraphics)]
    internal static extern IntPtr CGEventCreateMouseEvent(IntPtr source, uint mouseType, CGPoint mouseCursorPosition, uint mouseButton);

    [DllImport(CoreGraphics)]
    internal static extern IntPtr CGEventCreateScrollWheelEvent2(IntPtr source, uint units, uint wheelCount, int wheel1, int wheel2, int wheel3);

    [DllImport(CoreGraphics)]
    internal static extern IntPtr CGEventCreateKeyboardEvent(IntPtr source, ushort virtualKey, [MarshalAs(UnmanagedType.I1)] bool keyDown);

    [DllImport(CoreGraphics)]
    internal static extern void CGEventPost(uint tap, IntPtr evt);

    [DllImport(CoreGraphics)]
    internal static extern IntPtr CGEventTapCreate(uint tap, uint place, uint options, ulong eventsOfInterest, CGEventTapCallBack callback, IntPtr userInfo);

    [DllImport(CoreGraphics)]
    internal static extern void CGEventTapEnable(IntPtr tap, [MarshalAs(UnmanagedType.I1)] bool enable);

    // ---------- Símbolos globais (constantes exportadas) ----------

    /// <summary>Lê o valor de um símbolo global do tipo ponteiro (ex.: CFStringRef, CFBooleanRef).</summary>
    internal static IntPtr ReadGlobalPointer(string[] libraries, string symbol)
    {
        return Marshal.ReadIntPtr(GetGlobalAddress(libraries, symbol));
    }

    /// <summary>Endereço de um símbolo global (ex.: estruturas de callbacks do CoreFoundation).</summary>
    internal static IntPtr GetGlobalAddress(string[] libraries, string symbol)
    {
        foreach (string library in libraries)
        {
            if (NativeLibrary.TryLoad(library, out IntPtr handle) && NativeLibrary.TryGetExport(handle, symbol, out IntPtr address))
            {
                return address;
            }
        }
        throw new EntryPointNotFoundException($"Símbolo '{symbol}' não encontrado.");
    }

    internal static string[] CoreFoundationLibs => new[] { CoreFoundation };

    internal static string[] AccessibilityLibs => new[] { ApplicationServices, HIServices };

    /// <summary>kCFRunLoopCommonModes (CFStringRef).</summary>
    internal static IntPtr CommonModes => ReadGlobalPointer(CoreFoundationLibs, "kCFRunLoopCommonModes");

    /// <summary>kAXTrustedCheckOptionPrompt (CFStringRef).</summary>
    internal static IntPtr AXTrustedCheckOptionPrompt => ReadGlobalPointer(AccessibilityLibs, "kAXTrustedCheckOptionPrompt");

    /// <summary>kCFBooleanTrue (CFBooleanRef).</summary>
    internal static IntPtr CFBooleanTrue => ReadGlobalPointer(CoreFoundationLibs, "kCFBooleanTrue");

    /// <summary>Endereço de kCFTypeDictionaryKeyCallBacks (struct).</summary>
    internal static IntPtr CFTypeDictionaryKeyCallBacks => GetGlobalAddress(CoreFoundationLibs, "kCFTypeDictionaryKeyCallBacks");

    /// <summary>Endereço de kCFTypeDictionaryValueCallBacks (struct).</summary>
    internal static IntPtr CFTypeDictionaryValueCallBacks => GetGlobalAddress(CoreFoundationLibs, "kCFTypeDictionaryValueCallBacks");

    /// <summary>Posição atual do cursor em coordenadas globais. Retorna (0,0) se não for possível ler.</summary>
    internal static CGPoint GetCursorLocation()
    {
        IntPtr evt = CGEventCreate(IntPtr.Zero);
        if (evt == IntPtr.Zero)
        {
            return new CGPoint(0, 0);
        }
        try
        {
            return CGEventGetLocation(evt);
        }
        finally
        {
            CFRelease(evt);
        }
    }
}
