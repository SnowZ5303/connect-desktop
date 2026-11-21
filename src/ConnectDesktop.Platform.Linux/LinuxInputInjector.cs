#nullable enable
using System;
using ConnectDesktop.Core.Platform;
using CoreMouseButton = ConnectDesktop.Core.Platform.MouseButton;

namespace ConnectDesktop.Platform.Linux;

/// <summary>Injeção de mouse e teclado via XTest. Todas as chamadas são serializadas por um lock interno.</summary>
internal sealed class LinuxInputInjector : IInputInjector, IDisposable
{
    private const int WheelStep = 120;

    private readonly object _lock = new();
    private readonly XDisplay _display;
    private int _wheelRemainderX;
    private int _wheelRemainderY;

    public LinuxInputInjector()
    {
        _display = XDisplay.TryOpen() ?? throw new InvalidOperationException(XDisplay.OpenErrorMessage);
    }

    public void MoveMouseAbsolute(int x, int y)
    {
        lock (_lock)
        {
            XNative.XTestFakeMotionEvent(_display.Handle, -1, x, y, 0);
            XNative.XFlush(_display.Handle);
        }
    }

    public void MouseButton(CoreMouseButton button, bool down)
    {
        // Botões X11: 1 = esquerdo, 2 = meio, 3 = direito, 8 = X1, 9 = X2.
        uint xButton = button switch
        {
            CoreMouseButton.Left => 1u,
            CoreMouseButton.Middle => 2u,
            CoreMouseButton.Right => 3u,
            CoreMouseButton.X1 => 8u,
            CoreMouseButton.X2 => 9u,
            _ => 0u,
        };
        if (xButton == 0) return;

        lock (_lock)
        {
            FakeButton(xButton, down);
        }
    }

    public void MouseWheel(int dx, int dy)
    {
        lock (_lock)
        {
            // Acumula o resto para não perder movimento de rolagem fracionada.
            _wheelRemainderY += dy;
            int clicksY = _wheelRemainderY / WheelStep;
            _wheelRemainderY %= WheelStep;

            _wheelRemainderX += dx;
            int clicksX = _wheelRemainderX / WheelStep;
            _wheelRemainderX %= WheelStep;

            // 4 = cima, 5 = baixo, 6 = esquerda, 7 = direita.
            uint verticalButton = clicksY > 0 ? 4u : 5u;
            for (int i = 0; i < Math.Abs(clicksY); i++)
            {
                FakeButton(verticalButton, true);
                FakeButton(verticalButton, false);
            }

            uint horizontalButton = clicksX > 0 ? 7u : 6u;
            for (int i = 0; i < Math.Abs(clicksX); i++)
            {
                FakeButton(horizontalButton, true);
                FakeButton(horizontalButton, false);
            }
        }
    }

    public void Key(ushort hid, bool down)
    {
        uint? keycode = KeyMap.HidToX11(hid);
        if (keycode is not uint code) return;

        lock (_lock)
        {
            XNative.XTestFakeKeyEvent(_display.Handle, code, down ? 1 : 0, 0);
            XNative.XFlush(_display.Handle);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _display.Dispose();
        }
    }

    // Chamar sempre com _lock adquirido.
    private void FakeButton(uint xButton, bool down)
    {
        XNative.XTestFakeButtonEvent(_display.Handle, xButton, down ? 1 : 0, 0);
        XNative.XFlush(_display.Handle);
    }
}
