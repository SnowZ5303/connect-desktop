#nullable enable
using System;
using System.Threading;
using ConnectDesktop.Core.Platform;

namespace ConnectDesktop.Platform.Linux;

/// <summary>
/// Captura global de mouse e teclado no X11.
/// Sem supressão: polling de XQueryPointer a cada 8 ms; DX/DY = diferença para a leitura anterior.
/// Com supressão: XGrabPointer + XGrabKeyboard, cursor estacionado no centro da tela e leitura de XEvent.
///
/// Offsets usados no XEvent (x86_64, 192 bytes; os campos ficam no mesmo lugar em XKeyEvent, XButtonEvent e XMotionEvent):
///   type (int)                 @ 0
///   time (Time = unsigned long) @ 56
///   x_root (int)               @ 72
///   y_root (int)               @ 76
///   keycode (XKeyEvent) /
///   button (XButtonEvent) (uint) @ 84
/// </summary>
internal sealed class LinuxInputCapture : IInputCapture
{
    private const int PollIntervalMs = 8;
    private const int GrabIdleMs = 2;
    private const int WheelStep = 120;
    private const int ThreadJoinTimeoutMs = 2000;
    private const long WarpTimeoutMs = 100;
    private const int EventSize = 192;

    // Tipos de evento (X.h)
    private const int KeyPress = 2;
    private const int KeyRelease = 3;
    private const int ButtonPress = 4;
    private const int ButtonRelease = 5;
    private const int MotionNotify = 6;

    // Máscaras (X.h)
    private const int ButtonPressMask = 1 << 2;
    private const int ButtonReleaseMask = 1 << 3;
    private const int PointerMotionMask = 1 << 6;

    private const int GrabModeAsync = 1;

    // Offsets dentro do XEvent (x86_64)
    private const int OffsetType = 0;
    private const int OffsetTime = 56;
    private const int OffsetXRoot = 72;
    private const int OffsetYRoot = 76;
    private const int OffsetKeycodeOrButton = 84;

    private volatile bool _suppressRequested;
    private volatile bool _stopRequested;
    private Thread? _thread;

    private readonly byte[] _eventBuffer = new byte[EventSize];
    private readonly byte[] _peekBuffer = new byte[EventSize];

    // Estado usado somente pela thread de captura
    private bool _grabbed;
    private int _screenWidth;
    private int _screenHeight;
    private int _centerX;
    private int _centerY;
    private int _virtualX;
    private int _virtualY;
    private bool _havePosition;
    private int _lastX;
    private int _lastY;
    private bool _warpPending;
    private long _warpTickMs;

    public event Action<MouseMoveEvent>? MouseMoved;
    public event Action<MouseButtonEvent>? MouseButtonChanged;
    public event Action<MouseWheelEvent>? MouseWheel;
    public event Action<KeyEvent>? KeyChanged;

    public bool SuppressLocalInput
    {
        get => _suppressRequested;
        set => _suppressRequested = value;
    }

    public void Start()
    {
        if (_thread != null) return;

        XDisplay display = XDisplay.TryOpen()
            ?? throw new InvalidOperationException(XDisplay.OpenErrorMessage);

        _stopRequested = false;
        _thread = new Thread(() => Run(display))
        {
            IsBackground = true,
            Name = "Connect Desktop: captura X11",
        };
        _thread.Start();
    }

    public void Stop()
    {
        Thread? thread = _thread;
        if (thread == null) return;

        _stopRequested = true;
        if (thread != Thread.CurrentThread)
        {
            thread.Join(ThreadJoinTimeoutMs);
        }
        _thread = null;
    }

    public void Dispose()
    {
        Stop();
    }

    private void Run(XDisplay display)
    {
        try
        {
            while (!_stopRequested)
            {
                try
                {
                    bool want = _suppressRequested;
                    if (want && !_grabbed)
                    {
                        _grabbed = EnableGrab(display);
                    }
                    else if (!want && _grabbed)
                    {
                        DisableGrab(display);
                    }

                    if (_grabbed)
                    {
                        PumpEvents(display);
                    }
                    else
                    {
                        PollPointer(display);
                        Thread.Sleep(PollIntervalMs);
                    }
                }
                catch (Exception)
                {
                    Thread.Sleep(PollIntervalMs);
                }
            }
        }
        finally
        {
            if (_grabbed)
            {
                DisableGrab(display);
            }
            display.Dispose();
        }
    }

    private bool EnableGrab(XDisplay display)
    {
        IntPtr dpy = display.Handle;
        nuint root = display.Root;

        if (XNative.XGrabPointer(dpy, root, 0,
                ButtonPressMask | ButtonReleaseMask | PointerMotionMask,
                GrabModeAsync, GrabModeAsync, 0, 0, 0) != 0)
        {
            return false;
        }

        if (XNative.XGrabKeyboard(dpy, root, 0, GrabModeAsync, GrabModeAsync, 0) != 0)
        {
            XNative.XUngrabPointer(dpy, 0);
            XNative.XFlush(dpy);
            return false;
        }

        _screenWidth = XNative.XDisplayWidth(dpy, display.Screen);
        _screenHeight = XNative.XDisplayHeight(dpy, display.Screen);
        _centerX = _screenWidth / 2;
        _centerY = _screenHeight / 2;

        // Posição virtual parte da posição real atual do cursor.
        if (XNative.XQueryPointer(dpy, root, out _, out _, out int rootX, out int rootY,
                out _, out _, out _) != 0)
        {
            _virtualX = rootX;
            _virtualY = rootY;
        }

        _lastX = _virtualX;
        _lastY = _virtualY;
        _havePosition = false;
        _warpPending = false;
        if (_virtualX != _centerX || _virtualY != _centerY)
        {
            WarpToCenter(display);
        }
        return true;
    }

    private void DisableGrab(XDisplay display)
    {
        IntPtr dpy = display.Handle;
        XNative.XUngrabKeyboard(dpy, 0);
        XNative.XUngrabPointer(dpy, 0);
        XNative.XFlush(dpy);
        _grabbed = false;
        _havePosition = false;
        _warpPending = false;
    }

    private void PumpEvents(XDisplay display)
    {
        IntPtr dpy = display.Handle;
        CheckWarpTimeout(display);
        if (XNative.XPending(dpy) <= 0)
        {
            Thread.Sleep(GrabIdleMs);
            return;
        }

        // Drena a fila inteira; um KeyRelease pode consumir o KeyPress de autorepeat seguinte.
        while (XNative.XPending(dpy) > 0)
        {
            XNative.XNextEvent(dpy, _eventBuffer);
            HandleEvent(display, _eventBuffer);
        }
    }

    private void HandleEvent(XDisplay display, byte[] e)
    {
        int type = BitConverter.ToInt32(e, OffsetType);
        switch (type)
        {
            case MotionNotify:
            {
                int xRoot = BitConverter.ToInt32(e, OffsetXRoot);
                int yRoot = BitConverter.ToInt32(e, OffsetYRoot);

                // MotionNotify exatamente no centro com warp pendente: é o evento do próprio warp.
                // Ancora a última posição no centro e não emite evento.
                if (_warpPending && xRoot == _centerX && yRoot == _centerY)
                {
                    _lastX = _centerX;
                    _lastY = _centerY;
                    _warpPending = false;
                    return;
                }

                // Demais eventos (inclusive os antigos que estavam na fila antes do warp):
                // DX/DY pela última posição vista, nunca pelo centro.
                int dx = xRoot - _lastX;
                int dy = yRoot - _lastY;
                _lastX = xRoot;
                _lastY = yRoot;
                if (dx == 0 && dy == 0) return;

                _virtualX = Math.Clamp(_virtualX + dx, 0, _screenWidth - 1);
                _virtualY = Math.Clamp(_virtualY + dy, 0, _screenHeight - 1);
                MouseMoved?.Invoke(new MouseMoveEvent(_virtualX, _virtualY, dx, dy));

                // Só re-estaciona no centro quando o cursor se afasta mais de 1/4 da tela,
                // e nunca enquanto um warp anterior ainda estiver pendente.
                if (!_warpPending &&
                    (Math.Abs(xRoot - _centerX) > _screenWidth / 4 || Math.Abs(yRoot - _centerY) > _screenHeight / 4))
                {
                    WarpToCenter(display);
                }
                return;
            }

            case ButtonPress:
            case ButtonRelease:
            {
                uint button = BitConverter.ToUInt32(e, OffsetKeycodeOrButton);
                bool down = type == ButtonPress;

                // 4 = cima, 5 = baixo, 6 = esquerda, 7 = direita. Roda emite só no press.
                if (button >= 4 && button <= 7)
                {
                    if (!down) return;
                    switch (button)
                    {
                        case 4: MouseWheel?.Invoke(new MouseWheelEvent(0, WheelStep)); break;
                        case 5: MouseWheel?.Invoke(new MouseWheelEvent(0, -WheelStep)); break;
                        case 6: MouseWheel?.Invoke(new MouseWheelEvent(-WheelStep, 0)); break;
                        case 7: MouseWheel?.Invoke(new MouseWheelEvent(WheelStep, 0)); break;
                    }
                    return;
                }

                MouseButton? mouseButton = ToMouseButton(button);
                if (mouseButton is MouseButton b)
                {
                    MouseButtonChanged?.Invoke(new MouseButtonEvent(b, down));
                }
                return;
            }

            case KeyPress:
            case KeyRelease:
            {
                uint keycode = BitConverter.ToUInt32(e, OffsetKeycodeOrButton);
                ushort? hid = KeyMap.X11ToHid(keycode);
                if (hid is not ushort code) return;

                if (type == KeyRelease)
                {
                    ulong time = BitConverter.ToUInt64(e, OffsetTime);
                    // Autorepeat do X11: KeyRelease seguido de KeyPress com mesmo keycode e time.
                    // Descarta o release e emite só o KeyPress de repetição.
                    if (TryConsumeAutorepeat(display, keycode, time))
                    {
                        KeyChanged?.Invoke(new KeyEvent(code, true));
                        return;
                    }
                }

                KeyChanged?.Invoke(new KeyEvent(code, type == KeyPress));
                return;
            }
        }
    }

    private void PollPointer(XDisplay display)
    {
        if (XNative.XQueryPointer(display.Handle, display.Root, out _, out _,
                out int rootX, out int rootY, out _, out _, out _) == 0)
        {
            return;
        }

        if (!_havePosition)
        {
            _lastX = rootX;
            _lastY = rootY;
            _havePosition = true;
            return;
        }

        if (rootX == _lastX && rootY == _lastY) return;

        int dx = rootX - _lastX;
        int dy = rootY - _lastY;
        _lastX = rootX;
        _lastY = rootY;
        MouseMoved?.Invoke(new MouseMoveEvent(rootX, rootY, dx, dy));
    }

    private void WarpToCenter(XDisplay display)
    {
        XNative.XWarpPointer(display.Handle, 0, display.Root, 0, 0, 0, 0, _centerX, _centerY);
        XNative.XFlush(display.Handle);
        _warpPending = true;
        _warpTickMs = Environment.TickCount64;
    }

    /// <summary>
    /// Se o evento do warp não chegou em até 100 ms, desliga _warpPending e reancora
    /// a última posição na posição atual do cursor, para ele não ficar derivando.
    /// </summary>
    private void CheckWarpTimeout(XDisplay display)
    {
        if (!_warpPending || Environment.TickCount64 - _warpTickMs <= WarpTimeoutMs) return;

        _warpPending = false;
        if (XNative.XQueryPointer(display.Handle, display.Root, out _, out _,
                out int rootX, out int rootY, out _, out _, out _) != 0)
        {
            _lastX = rootX;
            _lastY = rootY;
        }
    }

    /// <summary>
    /// Chamado com um KeyRelease. Se o próximo evento da fila for um KeyPress com o mesmo keycode e time,
    /// ele é consumido (é a repetição automática) e retorna true.
    /// </summary>
    private bool TryConsumeAutorepeat(XDisplay display, uint keycode, ulong time)
    {
        IntPtr dpy = display.Handle;
        if (XNative.XPending(dpy) <= 0) return false;

        XNative.XPeekEvent(dpy, _peekBuffer);
        if (BitConverter.ToInt32(_peekBuffer, OffsetType) != KeyPress) return false;
        if (BitConverter.ToUInt32(_peekBuffer, OffsetKeycodeOrButton) != keycode) return false;
        if (BitConverter.ToUInt64(_peekBuffer, OffsetTime) != time) return false;

        XNative.XNextEvent(dpy, _peekBuffer);
        return true;
    }

    private static MouseButton? ToMouseButton(uint xButton)
    {
        return xButton switch
        {
            1 => MouseButton.Left,
            2 => MouseButton.Middle,
            3 => MouseButton.Right,
            8 => MouseButton.X1,
            9 => MouseButton.X2,
            _ => null,
        };
    }
}
