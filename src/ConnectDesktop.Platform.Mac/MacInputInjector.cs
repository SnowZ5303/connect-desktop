// Dono: g04. Injeção de mouse e teclado no macOS (CGEventPost no HID system tap).
using ConnectDesktop.Core.Platform;

namespace ConnectDesktop.Platform.Mac;

public sealed class MacInputInjector : IInputInjector
{
    private const uint HIDSystemState = 1;
    private const uint HIDEventTap = 0;
    private const uint ScrollEventUnitLine = 1;
    private const int WheelDelta = 120;
    private const long DoubleClickIntervalMs = 500;
    private const double DoubleClickDistancePx = 4;

    private readonly object _sync = new();
    private readonly HashSet<MouseButton> _pressed = new();
    private readonly Dictionary<MouseButton, DownInfo> _lastDown = new();
    private IntPtr _source;
    private int _wheelAccX;
    private int _wheelAccY;

    /// <summary>Último "down" de cada botão, para contagem de cliques (duplo clique).</summary>
    private readonly record struct DownInfo(long Time, double X, double Y, int Count);

    private IntPtr Source()
    {
        if (_source == IntPtr.Zero)
        {
            _source = MacNative.CGEventSourceCreate(HIDSystemState);
            if (_source == IntPtr.Zero)
            {
                throw new InvalidOperationException("Não foi possível criar a fonte de eventos do macOS.");
            }
        }
        return _source;
    }

    public void MoveMouseAbsolute(int x, int y)
    {
        lock (_sync)
        {
            uint type = CGEventTypes.MouseMoved;
            MacButtonInfo info = default;
            foreach (MouseButton button in MacButtonMap.All)
            {
                if (_pressed.Contains(button))
                {
                    info = MacButtonMap.Info(button);
                    type = info.Dragged;
                    break;
                }
            }

            IntPtr ev = MacNative.CGEventCreateMouseEvent(Source(), type, new CGPoint(x, y), info.CgButton);
            if (ev == IntPtr.Zero)
            {
                return;
            }
            MacNative.CGEventSetIntegerValueField(ev, CGEventFields.MouseEventButtonNumber, info.Number);
            Post(ev);
        }
    }

    public void MouseButton(MouseButton button, bool down)
    {
        lock (_sync)
        {
            MacButtonInfo info = MacButtonMap.Info(button);
            CGPoint pos = MacNative.GetCursorLocation();
            int count = 1;

            if (down)
            {
                long now = Environment.TickCount64;
                if (_lastDown.TryGetValue(button, out DownInfo previous)
                    && now - previous.Time < DoubleClickIntervalMs
                    && Math.Abs(pos.X - previous.X) < DoubleClickDistancePx
                    && Math.Abs(pos.Y - previous.Y) < DoubleClickDistancePx)
                {
                    count = previous.Count + 1;
                }
                _lastDown[button] = new DownInfo(now, pos.X, pos.Y, count);
                _pressed.Add(button);
            }
            else
            {
                if (_lastDown.TryGetValue(button, out DownInfo previous))
                {
                    count = previous.Count;
                }
                _pressed.Remove(button);
            }

            uint type = down ? info.Down : info.Up;
            IntPtr ev = MacNative.CGEventCreateMouseEvent(Source(), type, pos, info.CgButton);
            if (ev == IntPtr.Zero)
            {
                return;
            }
            MacNative.CGEventSetIntegerValueField(ev, CGEventFields.MouseEventButtonNumber, info.Number);
            MacNative.CGEventSetIntegerValueField(ev, CGEventFields.MouseEventClickState, count);
            Post(ev);
        }
    }

    public void MouseWheel(int dx, int dy)
    {
        lock (_sync)
        {
            // Acumula o resto para que rolagens menores que um clique não se percam.
            _wheelAccX += dx;
            _wheelAccY += dy;
            int linesX = _wheelAccX / WheelDelta;
            int linesY = _wheelAccY / WheelDelta;
            _wheelAccX -= linesX * WheelDelta;
            _wheelAccY -= linesY * WheelDelta;
            if (linesX == 0 && linesY == 0)
            {
                return;
            }

            IntPtr ev = MacNative.CGEventCreateScrollWheelEvent2(
                Source(),
                ScrollEventUnitLine,
                2,
                linesY,
                linesX,
                0);
            if (ev == IntPtr.Zero)
            {
                return;
            }
            Post(ev);
        }
    }

    public void Key(ushort hid, bool down)
    {
        lock (_sync)
        {
            ushort? keycode = KeyMap.HidToMac(hid);
            if (keycode is not ushort kc)
            {
                return;
            }
            IntPtr ev = MacNative.CGEventCreateKeyboardEvent(Source(), kc, down);
            if (ev == IntPtr.Zero)
            {
                return;
            }
            Post(ev);
        }
    }

    /// <summary>Envia o evento no HID tap e libera a referência (CFRelease).</summary>
    private static void Post(IntPtr ev)
    {
        try
        {
            MacNative.CGEventPost(HIDEventTap, ev);
        }
        finally
        {
            MacNative.CFRelease(ev);
        }
    }
}

/// <summary>Dados de um botão do mouse no macOS.</summary>
internal readonly record struct MacButtonInfo(uint Down, uint Up, uint Dragged, uint CgButton, long Number);

/// <summary>Mapeamento de <see cref="MouseButton"/> para tipos de evento e número de botão do CoreGraphics.</summary>
internal static class MacButtonMap
{
    /// <summary>Ordem de prioridade para escolher o botão usado num arraste.</summary>
    internal static IEnumerable<MouseButton> All => new[]
    {
        MouseButton.Left,
        MouseButton.Right,
        MouseButton.Middle,
        MouseButton.X1,
        MouseButton.X2,
    };

    internal static MacButtonInfo Info(MouseButton button)
    {
        return button switch
        {
            MouseButton.Left => new MacButtonInfo(CGEventTypes.LeftMouseDown, CGEventTypes.LeftMouseUp, CGEventTypes.LeftMouseDragged, 0, 0),
            MouseButton.Right => new MacButtonInfo(CGEventTypes.RightMouseDown, CGEventTypes.RightMouseUp, CGEventTypes.RightMouseDragged, 1, 1),
            MouseButton.Middle => new MacButtonInfo(CGEventTypes.OtherMouseDown, CGEventTypes.OtherMouseUp, CGEventTypes.OtherMouseDragged, 2, 2),
            MouseButton.X1 => new MacButtonInfo(CGEventTypes.OtherMouseDown, CGEventTypes.OtherMouseUp, CGEventTypes.OtherMouseDragged, 2, 3),
            MouseButton.X2 => new MacButtonInfo(CGEventTypes.OtherMouseDown, CGEventTypes.OtherMouseUp, CGEventTypes.OtherMouseDragged, 2, 4),
            _ => throw new ArgumentOutOfRangeException(nameof(button)),
        };
    }
}
