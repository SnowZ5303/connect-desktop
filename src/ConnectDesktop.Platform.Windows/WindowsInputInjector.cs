using System;
using System.Runtime.InteropServices;
using ConnectDesktop.Core.Platform;
using CoreMouseButton = ConnectDesktop.Core.Platform.MouseButton;

namespace ConnectDesktop.Platform.Windows;

/// <summary>
/// Injeta mouse e teclado no Windows via SendInput.
/// Janelas de administrador não recebem input injetado, a menos que este app também rode como administrador.
/// A tecla Pause é injetada pelo código virtual VK_PAUSE, porque o scan code 0x45 é o do NumLock.
/// </summary>
public sealed class WindowsInputInjector : IInputInjector
{
    private const ushort VkPause = 0x13;
    private const ushort HidPause = 0x48;

    /// <summary>Move o cursor para (x, y) em pixels da área de trabalho virtual.</summary>
    public void MoveMouseAbsolute(int x, int y)
    {
        int vx = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        int vy = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        int vw = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        int vh = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);

        long nx = (long)(x - vx) * 65535 / Math.Max(vw - 1, 1);
        long ny = (long)(y - vy) * 65535 / Math.Max(vh - 1, 1);

        var input = new INPUT
        {
            type = NativeMethods.INPUT_MOUSE,
            mi = new MOUSEINPUT
            {
                dx = (int)Math.Clamp(nx, 0, 65535),
                dy = (int)Math.Clamp(ny, 0, 65535),
                mouseData = 0,
                dwFlags = NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE | NativeMethods.MOUSEEVENTF_VIRTUALDESK,
                time = 0,
                dwExtraInfo = IntPtr.Zero
            }
        };
        Send(input);
    }

    /// <summary>Pressiona ou solta um botão do mouse.</summary>
    public void MouseButton(CoreMouseButton button, bool down)
    {
        var input = new INPUT { type = NativeMethods.INPUT_MOUSE };
        uint flags;
        uint mouseData = 0;

        switch (button)
        {
            case CoreMouseButton.Left:
                flags = down ? NativeMethods.MOUSEEVENTF_LEFTDOWN : NativeMethods.MOUSEEVENTF_LEFTUP;
                break;
            case CoreMouseButton.Right:
                flags = down ? NativeMethods.MOUSEEVENTF_RIGHTDOWN : NativeMethods.MOUSEEVENTF_RIGHTUP;
                break;
            case CoreMouseButton.Middle:
                flags = down ? NativeMethods.MOUSEEVENTF_MIDDLEDOWN : NativeMethods.MOUSEEVENTF_MIDDLEUP;
                break;
            case CoreMouseButton.X1:
                flags = down ? NativeMethods.MOUSEEVENTF_XDOWN : NativeMethods.MOUSEEVENTF_XUP;
                mouseData = 1;
                break;
            case CoreMouseButton.X2:
                flags = down ? NativeMethods.MOUSEEVENTF_XDOWN : NativeMethods.MOUSEEVENTF_XUP;
                mouseData = 2;
                break;
            default:
                return;
        }

        input.mi = new MOUSEINPUT
        {
            dwFlags = flags,
            mouseData = mouseData
        };
        Send(input);
    }

    /// <summary>Rola a roda do mouse. Unidades: 120 = um clique. DY positivo = para cima.</summary>
    public void MouseWheel(int dx, int dy)
    {
        if (dy != 0)
        {
            Send(new INPUT
            {
                type = NativeMethods.INPUT_MOUSE,
                mi = new MOUSEINPUT { dwFlags = NativeMethods.MOUSEEVENTF_WHEEL, mouseData = unchecked((uint)dy) }
            });
        }

        if (dx != 0)
        {
            Send(new INPUT
            {
                type = NativeMethods.INPUT_MOUSE,
                mi = new MOUSEINPUT { dwFlags = NativeMethods.MOUSEEVENTF_HWHEEL, mouseData = unchecked((uint)dx) }
            });
        }
    }

    /// <summary>Pressiona ou solta uma tecla pelo código HID. Se o HID não tiver equivalente, nada é injetado.</summary>
    public void Key(ushort hid, bool down)
    {
        uint upFlag = down ? 0u : NativeMethods.KEYEVENTF_KEYUP;

        if (hid == HidPause)
        {
            Send(new INPUT
            {
                type = NativeMethods.INPUT_KEYBOARD,
                ki = new KEYBDINPUT { wVk = VkPause, dwFlags = upFlag }
            });
            return;
        }

        var mapped = KeyMap.HidToScan(hid);
        if (mapped is not { } scan)
        {
            return;
        }

        uint flags = NativeMethods.KEYEVENTF_SCANCODE | upFlag;
        if (scan.extended)
        {
            flags |= NativeMethods.KEYEVENTF_EXTENDEDKEY;
        }

        Send(new INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD,
            ki = new KEYBDINPUT { wScan = scan.scan, dwFlags = flags }
        });
    }

    private static void Send(INPUT input)
    {
        NativeMethods.SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }
}
