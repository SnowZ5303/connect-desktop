using ConnectDesktop.Core.Platform;

namespace ConnectDesktop.Platform.Windows;

/// <summary>
/// Informações de tela e posição do cursor no Windows.
/// O app roda com DPI PerMonitorV2 (manifest), então todas as coordenadas são em pixels físicos.
/// </summary>
public sealed class WindowsScreenInfo : IScreenInfo
{
    /// <summary>Retângulo que envolve todos os monitores (área de trabalho virtual), em pixels físicos.</summary>
    public ScreenBounds GetDesktopBounds()
    {
        int x = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        int y = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        int width = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        int height = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);
        return new ScreenBounds(x, y, width, height);
    }

    /// <summary>Posição atual do cursor. Retorna (0, 0) se o Windows não conseguir informar.</summary>
    public (int X, int Y) GetCursorPosition()
    {
        if (NativeMethods.GetCursorPos(out var point))
        {
            return (point.X, point.Y);
        }
        return (0, 0);
    }

    /// <summary>Move o cursor local (sem gerar evento de captura relevante).</summary>
    public void SetCursorPosition(int x, int y)
    {
        NativeMethods.SetCursorPos(x, y);
    }
}
