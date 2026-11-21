// Dono: g04. Captura global de mouse e teclado no macOS (CGEventTap em thread própria com CFRunLoop).
using ConnectDesktop.Core.Platform;

namespace ConnectDesktop.Platform.Mac;

public sealed class MacInputCapture : IInputCapture
{
    private const uint SessionEventTap = 1;        // kCGSessionEventTap
    private const uint HeadInsertEventTap = 0;     // kCGHeadInsertEventTap
    private const uint DefaultOptions = 0;         // kCGEventTapOptionDefault
    private const int StopTimeoutMs = 2000;

    private static readonly ulong EventMask =
        Bit(CGEventTypes.MouseMoved)
        | Bit(CGEventTypes.LeftMouseDown) | Bit(CGEventTypes.LeftMouseUp)
        | Bit(CGEventTypes.RightMouseDown) | Bit(CGEventTypes.RightMouseUp)
        | Bit(CGEventTypes.LeftMouseDragged) | Bit(CGEventTypes.RightMouseDragged)
        | Bit(CGEventTypes.KeyDown) | Bit(CGEventTypes.KeyUp) | Bit(CGEventTypes.FlagsChanged)
        | Bit(CGEventTypes.ScrollWheel)
        | Bit(CGEventTypes.OtherMouseDown) | Bit(CGEventTypes.OtherMouseUp) | Bit(CGEventTypes.OtherMouseDragged);

    private readonly object _sync = new();
    private volatile bool _suppress;
    private bool _running;
    private bool _associated = true;
    private Thread? _thread;
    private IntPtr _tap;
    private IntPtr _loop;
    private Exception? _startError;
    private MacNative.CGEventTapCallBack? _callback; // mantido vivo para o GC não coletar o delegate

    public event Action<MouseMoveEvent>? MouseMoved;
    public event Action<MouseButtonEvent>? MouseButtonChanged;
    public event Action<MouseWheelEvent>? MouseWheel;
    public event Action<KeyEvent>? KeyChanged;

    /// <summary>true = eventos engolidos e cursor local parado (desassocia o mouse do cursor).</summary>
    public bool SuppressLocalInput
    {
        get => _suppress;
        set
        {
            lock (_sync)
            {
                _suppress = value;
                SyncAssociation();
            }
        }
    }

    /// <summary>
    /// Inicia a captura. Lança InvalidOperationException com a mensagem de permissão se faltar Acessibilidade
    /// ou se o tap não puder ser criado; PlatformNotSupportedException se a API nativa não existir.
    /// </summary>
    public void Start()
    {
        Thread thread;
        ManualResetEventSlim ready = new(false);
        lock (_sync)
        {
            if (_thread != null)
            {
                return;
            }
            if (!MacPermissions.IsTrusted())
            {
                throw new InvalidOperationException(MacPermissions.PermissionMessage);
            }
            _startError = null;
            _running = true;
            thread = new Thread(() => RunLoop(ready))
            {
                IsBackground = true,
                Name = "ConnectDesktop.MacInputCapture",
            };
            _thread = thread;
        }

        thread.Start();
        ready.Wait();

        if (_startError != null)
        {
            lock (_sync)
            {
                _running = false;
                _thread = null;
                SyncAssociation();
            }
            thread.Join(StopTimeoutMs);
            throw _startError;
        }

        lock (_sync)
        {
            SyncAssociation();
        }
    }

    public void Stop()
    {
        Thread? thread;
        lock (_sync)
        {
            _running = false;
            thread = _thread;
            // Invalidar o tap remove a fonte do runloop; CFRunLoopStop garante a saída do CFRunLoopRun.
            if (_tap != IntPtr.Zero)
            {
                MacNative.CFMachPortInvalidate(_tap);
            }
            if (_loop != IntPtr.Zero)
            {
                MacNative.CFRunLoopStop(_loop);
            }
            SyncAssociation();
        }

        if (thread != null && thread != Thread.CurrentThread)
        {
            thread.Join(StopTimeoutMs);
        }

        lock (_sync)
        {
            if (_thread == thread)
            {
                _thread = null;
            }
        }
    }

    public void Dispose()
    {
        Stop();
    }

    /// <summary>Desassocia mouse e cursor só enquanto a captura roda com supressão ligada. Chamar com _sync travado.</summary>
    private void SyncAssociation()
    {
        bool connected = !_running || !_suppress;
        if (connected == _associated)
        {
            return;
        }
        MacNative.CGAssociateMouseAndMouseCursorPosition(connected);
        _associated = connected;
    }

    private void RunLoop(ManualResetEventSlim ready)
    {
        IntPtr tap = IntPtr.Zero;
        IntPtr source = IntPtr.Zero;
        try
        {
            _callback = OnEvent;
            tap = MacNative.CGEventTapCreate(SessionEventTap, HeadInsertEventTap, DefaultOptions, EventMask, _callback, IntPtr.Zero);
            if (tap == IntPtr.Zero)
            {
                _startError = new InvalidOperationException(MacPermissions.PermissionMessage);
                return;
            }

            source = MacNative.CFMachPortCreateRunLoopSource(IntPtr.Zero, tap, 0);
            if (source == IntPtr.Zero)
            {
                _startError = new InvalidOperationException("Não foi possível criar a fonte de eventos do macOS.");
                return;
            }

            IntPtr loop = MacNative.CFRunLoopGetCurrent();
            MacNative.CFRunLoopAddSource(loop, source, MacNative.CommonModes);
            MacNative.CGEventTapEnable(tap, true);

            bool stopNow;
            lock (_sync)
            {
                _tap = tap;
                _loop = loop;
                stopNow = !_running;
            }
            ready.Set();

            if (!stopNow)
            {
                MacNative.CFRunLoopRun();
            }
        }
        catch (DllNotFoundException ex)
        {
            _startError = new PlatformNotSupportedException("Captura de entrada do macOS indisponível neste sistema.", ex);
        }
        catch (EntryPointNotFoundException ex)
        {
            _startError = new PlatformNotSupportedException("Captura de entrada do macOS indisponível neste sistema.", ex);
        }
        catch (Exception ex)
        {
            _startError ??= ex;
        }
        finally
        {
            ready.Set();
            lock (_sync)
            {
                _tap = IntPtr.Zero;
                _loop = IntPtr.Zero;
            }
            if (tap != IntPtr.Zero)
            {
                MacNative.CFMachPortInvalidate(tap);
                MacNative.CFRelease(tap);
            }
            if (source != IntPtr.Zero)
            {
                MacNative.CFRelease(source);
            }
        }
    }

    private IntPtr OnEvent(IntPtr proxy, uint type, IntPtr evt, IntPtr refcon)
    {
        if (type == CGEventTypes.TapDisabledByTimeout || type == CGEventTypes.TapDisabledByUserInput)
        {
            ReenableTap();
            return evt;
        }

        try
        {
            Dispatch(type, evt);
        }
        catch
        {
            // exceções não podem atravessar a fronteira nativa; o evento segue o fluxo normal
        }

        return _suppress ? IntPtr.Zero : evt;
    }

    private void ReenableTap()
    {
        try
        {
            IntPtr tap;
            lock (_sync)
            {
                tap = _tap;
            }
            if (tap != IntPtr.Zero)
            {
                MacNative.CGEventTapEnable(tap, true);
            }
        }
        catch
        {
            // ignora falha ao reabilitar
        }
    }

    private void Dispatch(uint type, IntPtr evt)
    {
        switch (type)
        {
            case CGEventTypes.MouseMoved:
            case CGEventTypes.LeftMouseDragged:
            case CGEventTypes.RightMouseDragged:
            case CGEventTypes.OtherMouseDragged:
                RaiseMove(evt);
                break;

            case CGEventTypes.LeftMouseDown:
                MouseButtonChanged?.Invoke(new MouseButtonEvent(MouseButton.Left, true));
                break;
            case CGEventTypes.LeftMouseUp:
                MouseButtonChanged?.Invoke(new MouseButtonEvent(MouseButton.Left, false));
                break;
            case CGEventTypes.RightMouseDown:
                MouseButtonChanged?.Invoke(new MouseButtonEvent(MouseButton.Right, true));
                break;
            case CGEventTypes.RightMouseUp:
                MouseButtonChanged?.Invoke(new MouseButtonEvent(MouseButton.Right, false));
                break;
            case CGEventTypes.OtherMouseDown:
                RaiseOtherButton(evt, true);
                break;
            case CGEventTypes.OtherMouseUp:
                RaiseOtherButton(evt, false);
                break;

            case CGEventTypes.ScrollWheel:
                long axis1 = MacNative.CGEventGetIntegerValueField(evt, CGEventFields.ScrollWheelEventDeltaAxis1);
                long axis2 = MacNative.CGEventGetIntegerValueField(evt, CGEventFields.ScrollWheelEventDeltaAxis2);
                MouseWheel?.Invoke(new MouseWheelEvent((int)(axis2 * 120), (int)(axis1 * 120)));
                break;

            case CGEventTypes.KeyDown:
                RaiseKey(evt, true);
                break;
            case CGEventTypes.KeyUp:
                RaiseKey(evt, false);
                break;
            case CGEventTypes.FlagsChanged:
                RaiseFlagsChanged(evt);
                break;
        }
    }

    private void RaiseMove(IntPtr evt)
    {
        CGPoint location = MacNative.CGEventGetLocation(evt);
        int dx = (int)MacNative.CGEventGetIntegerValueField(evt, CGEventFields.MouseEventDeltaX);
        int dy = (int)MacNative.CGEventGetIntegerValueField(evt, CGEventFields.MouseEventDeltaY);
        MouseMoved?.Invoke(new MouseMoveEvent((int)Math.Round(location.X), (int)Math.Round(location.Y), dx, dy));
    }

    private void RaiseOtherButton(IntPtr evt, bool down)
    {
        long number = MacNative.CGEventGetIntegerValueField(evt, CGEventFields.MouseEventButtonNumber);
        MouseButton? button = number switch
        {
            2 => MouseButton.Middle,
            3 => MouseButton.X1,
            4 => MouseButton.X2,
            _ => null,
        };
        if (button is MouseButton b)
        {
            MouseButtonChanged?.Invoke(new MouseButtonEvent(b, down));
        }
    }

    private void RaiseKey(IntPtr evt, bool down)
    {
        ushort keycode = (ushort)MacNative.CGEventGetIntegerValueField(evt, CGEventFields.KeyboardEventKeycode);
        ushort? hid = KeyMap.MacToHid(keycode);
        if (hid is ushort h)
        {
            KeyChanged?.Invoke(new KeyEvent(h, down));
        }
    }

    private void RaiseFlagsChanged(IntPtr evt)
    {
        ushort keycode = (ushort)MacNative.CGEventGetIntegerValueField(evt, CGEventFields.KeyboardEventKeycode);
        ulong flag = ModifierFlag(keycode);
        if (flag == 0)
        {
            return;
        }
        bool down = (MacNative.CGEventGetFlags(evt) & flag) != 0;
        ushort? hid = KeyMap.MacToHid(keycode);
        if (hid is ushort h)
        {
            KeyChanged?.Invoke(new KeyEvent(h, down));
        }
    }

    /// <summary>Bit de CGEventFlags do modificador correspondente ao keycode (0 se não for modificador).</summary>
    private static ulong ModifierFlag(ushort keycode)
    {
        return keycode switch
        {
            0x38 or 0x3C => CGEventFlagMasks.Shift,
            0x3B or 0x3E => CGEventFlagMasks.Control,
            0x3A or 0x3D => CGEventFlagMasks.Alternate,
            0x37 or 0x36 => CGEventFlagMasks.Command,
            0x39 => CGEventFlagMasks.AlphaShift,
            _ => 0UL,
        };
    }

    private static ulong Bit(uint type) => 1UL << (int)type;
}
