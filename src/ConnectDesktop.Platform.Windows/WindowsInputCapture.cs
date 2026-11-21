using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using ConnectDesktop.Core.Platform;

namespace ConnectDesktop.Platform.Windows;

/// <summary>
/// Captura global de mouse e teclado via hooks de baixo nível (WH_MOUSE_LL e WH_KEYBOARD_LL).
/// Os hooks rodam numa thread dedicada com loop de mensagens próprio.
/// Com <see cref="SuppressLocalInput"/> ligado, os eventos são engolidos e o cursor fica estacionado
/// no centro da tela primária; DX/DY são calculados a partir desse ponto.
/// Os eventos são disparados na thread do hook: os assinantes devem ser rápidos e thread-safe.
/// </summary>
public sealed class WindowsInputCapture : IInputCapture
{
    private readonly object _sync = new();
    private LowLevelHookProc? _mouseProc;
    private LowLevelHookProc? _keyProc;
    private Thread? _thread;
    private uint _threadId;
    private string? _startFailure;
    private volatile bool _suppress;
    private int _lastX;
    private int _lastY;
    private int _parkX;
    private int _parkY;
    private bool _disposed;

    /// <inheritdoc />
    public event Action<MouseMoveEvent>? MouseMoved;

    /// <inheritdoc />
    public event Action<MouseButtonEvent>? MouseButtonChanged;

    /// <inheritdoc />
    public event Action<MouseWheelEvent>? MouseWheel;

    /// <inheritdoc />
    public event Action<KeyEvent>? KeyChanged;

    /// <inheritdoc />
    public bool SuppressLocalInput
    {
        get => _suppress;
        set
        {
            lock (_sync)
            {
                if (value == _suppress)
                {
                    return;
                }

                if (value)
                {
                    _parkX = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN) / 2;
                    _parkY = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN) / 2;
                    NativeMethods.SetCursorPos(_parkX, _parkY);
                    _lastX = _parkX;
                    _lastY = _parkY;
                }
                else if (NativeMethods.GetCursorPos(out var point))
                {
                    _lastX = point.X;
                    _lastY = point.Y;
                }

                _suppress = value;
            }
        }
    }

    /// <summary>
    /// Instala os hooks numa thread dedicada. Lança InvalidOperationException se o Windows recusar a instalação.
    /// </summary>
    public void Start()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(WindowsInputCapture));
        }

        var ready = new ManualResetEventSlim(false);
        lock (_sync)
        {
            if (_thread != null)
            {
                return;
            }

            _startFailure = null;
            var thread = new Thread(() => HookThreadMain(ready))
            {
                IsBackground = true,
                Name = "ConnectDesktop.InputCapture"
            };
            _thread = thread;
            thread.Start();
        }

        ready.Wait();
        ready.Dispose();

        string? failure;
        lock (_sync)
        {
            failure = _startFailure;
            if (failure != null)
            {
                _thread = null;
            }
        }

        if (failure != null)
        {
            throw new InvalidOperationException("Não foi possível instalar o hook de entrada: " + failure);
        }
    }

    /// <summary>Para a captura: posta WM_QUIT na thread do hook e desinstala os hooks.</summary>
    public void Stop()
    {
        Thread? thread;
        lock (_sync)
        {
            _suppress = false;
            thread = _thread;
        }

        if (thread == null)
        {
            return;
        }

        for (int attempt = 0; attempt < 20 && thread.IsAlive; attempt++)
        {
            NativeMethods.PostThreadMessage(_threadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            if (thread == Thread.CurrentThread || thread.Join(100))
            {
                break;
            }
        }

        lock (_sync)
        {
            if (_thread == thread)
            {
                _thread = null;
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }

    private void HookThreadMain(ManualResetEventSlim ready)
    {
        IntPtr mouseHook = IntPtr.Zero;
        IntPtr keyHook = IntPtr.Zero;
        string? failure = null;

        try
        {
            _threadId = NativeMethods.GetCurrentThreadId();
            IntPtr module = NativeMethods.GetModuleHandle(null);
            _mouseProc = MouseHookProc;
            _keyProc = KeyHookProc;

            mouseHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _mouseProc, module, 0);
            if (mouseHook == IntPtr.Zero)
            {
                failure = DescribeLastError("mouse (WH_MOUSE_LL)");
            }
            else
            {
                keyHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _keyProc, module, 0);
                if (keyHook == IntPtr.Zero)
                {
                    failure = DescribeLastError("teclado (WH_KEYBOARD_LL)");
                }
            }
        }
        catch (Exception ex)
        {
            failure = ex.Message;
        }

        if (failure != null)
        {
            if (mouseHook != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(mouseHook);
            }

            lock (_sync)
            {
                _startFailure = failure;
            }

            ready.Set();
            return;
        }

        ready.Set();

        try
        {
            while (NativeMethods.GetMessage(out _, IntPtr.Zero, 0, 0) > 0)
            {
            }
        }
        finally
        {
            if (keyHook != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(keyHook);
            }

            if (mouseHook != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(mouseHook);
            }
        }
    }

    private IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
        {
            return NativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        bool swallow;
        try
        {
            swallow = HandleMouse(wParam, lParam);
        }
        catch
        {
            swallow = _suppress;
        }

        return swallow ? (IntPtr)1 : NativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private IntPtr KeyHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
        {
            return NativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        bool swallow;
        try
        {
            swallow = HandleKey(wParam, lParam);
        }
        catch
        {
            swallow = _suppress;
        }

        return swallow ? (IntPtr)1 : NativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    /// <summary>Trata um evento de mouse. Retorna true se o evento deve ser engolido.</summary>
    private bool HandleMouse(IntPtr wParam, IntPtr lParam)
    {
        var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
        uint msg = (uint)wParam.ToInt64();
        bool injected = (data.flags & NativeMethods.LLMHF_INJECTED) != 0;

        if (msg == NativeMethods.WM_MOUSEMOVE)
        {
            MouseMoveEvent? move = null;
            bool swallow;
            lock (_sync)
            {
                swallow = _suppress;
                if (swallow && injected)
                {
                    // Eco do SetCursorPos da própria captura: ignorado.
                    return false;
                }

                if (swallow)
                {
                    int dx = data.pt.X - _parkX;
                    int dy = data.pt.Y - _parkY;
                    if (dx != 0 || dy != 0)
                    {
                        NativeMethods.SetCursorPos(_parkX, _parkY);
                        move = new MouseMoveEvent(data.pt.X, data.pt.Y, dx, dy);
                    }

                    _lastX = _parkX;
                    _lastY = _parkY;
                }
                else
                {
                    int dx = data.pt.X - _lastX;
                    int dy = data.pt.Y - _lastY;
                    _lastX = data.pt.X;
                    _lastY = data.pt.Y;
                    if (dx != 0 || dy != 0)
                    {
                        move = new MouseMoveEvent(data.pt.X, data.pt.Y, dx, dy);
                    }
                }
            }

            if (move is MouseMoveEvent value)
            {
                SafeInvoke(MouseMoved, value);
            }

            return swallow;
        }

        bool suppressed = _suppress;
        if (suppressed && injected)
        {
            return false;
        }

        switch (msg)
        {
            case NativeMethods.WM_LBUTTONDOWN:
                SafeInvoke(MouseButtonChanged, new MouseButtonEvent(MouseButton.Left, true));
                break;
            case NativeMethods.WM_LBUTTONUP:
                SafeInvoke(MouseButtonChanged, new MouseButtonEvent(MouseButton.Left, false));
                break;
            case NativeMethods.WM_RBUTTONDOWN:
                SafeInvoke(MouseButtonChanged, new MouseButtonEvent(MouseButton.Right, true));
                break;
            case NativeMethods.WM_RBUTTONUP:
                SafeInvoke(MouseButtonChanged, new MouseButtonEvent(MouseButton.Right, false));
                break;
            case NativeMethods.WM_MBUTTONDOWN:
                SafeInvoke(MouseButtonChanged, new MouseButtonEvent(MouseButton.Middle, true));
                break;
            case NativeMethods.WM_MBUTTONUP:
                SafeInvoke(MouseButtonChanged, new MouseButtonEvent(MouseButton.Middle, false));
                break;
            case NativeMethods.WM_XBUTTONDOWN:
            case NativeMethods.WM_XBUTTONUP:
                {
                    // mouseData HIWORD: 1 = X1, 2 = X2
                    ushort which = (ushort)((data.mouseData >> 16) & 0xFFFF);
                    var button = which == 2 ? MouseButton.X2 : MouseButton.X1;
                    bool down = msg == NativeMethods.WM_XBUTTONDOWN;
                    SafeInvoke(MouseButtonChanged, new MouseButtonEvent(button, down));
                    break;
                }
            case NativeMethods.WM_MOUSEWHEEL:
                {
                    short dy = (short)((data.mouseData >> 16) & 0xFFFF);
                    SafeInvoke(MouseWheel, new MouseWheelEvent(0, dy));
                    break;
                }
            case NativeMethods.WM_MOUSEHWHEEL:
                {
                    short dx = (short)((data.mouseData >> 16) & 0xFFFF);
                    SafeInvoke(MouseWheel, new MouseWheelEvent(dx, 0));
                    break;
                }
            default:
                return false;
        }

        return suppressed;
    }

    /// <summary>Trata um evento de teclado. Retorna true se o evento deve ser engolido.</summary>
    private bool HandleKey(IntPtr wParam, IntPtr lParam)
    {
        var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        uint msg = (uint)wParam.ToInt64();
        bool suppressed = _suppress;
        bool injected = (data.flags & NativeMethods.LLKHF_INJECTED) != 0;
        if (suppressed && injected)
        {
            return false;
        }

        bool down = msg == NativeMethods.WM_KEYDOWN || msg == NativeMethods.WM_SYSKEYDOWN;
        bool up = msg == NativeMethods.WM_KEYUP || msg == NativeMethods.WM_SYSKEYUP;
        if (!down && !up)
        {
            return false;
        }

        bool extended = (data.flags & NativeMethods.LLKHF_EXTENDED) != 0;
        ushort? hid = KeyMap.FromHookEvent(data.vkCode, data.scanCode, extended);
        if (hid is ushort code)
        {
            SafeInvoke(KeyChanged, new KeyEvent(code, down));
        }

        return suppressed;
    }

    private static void SafeInvoke<T>(Action<T>? handler, T value)
    {
        if (handler == null)
        {
            return;
        }

        try
        {
            handler(value);
        }
        catch
        {
            // Um assinante com erro não pode derrubar o hook.
        }
    }

    private static string DescribeLastError(string what)
    {
        int code = Marshal.GetLastWin32Error();
        return $"{what}: {new Win32Exception(code).Message} (erro {code})";
    }
}
