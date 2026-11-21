// Dono: g02. Assinaturas públicas FIXAS.
using ConnectDesktop.Core.Platform;

namespace ConnectDesktop.Core.Layout;

/// <summary>Lado em que o PC secundário fica, olhando a partir do principal.</summary>
public enum ScreenSide { Left, Right, Top, Bottom }

public static class ScreenLayout
{
    /// <summary>Lado oposto (Right->Left, Top->Bottom...).</summary>
    public static ScreenSide Opposite(ScreenSide side) => side switch
    {
        ScreenSide.Left => ScreenSide.Right,
        ScreenSide.Right => ScreenSide.Left,
        ScreenSide.Top => ScreenSide.Bottom,
        ScreenSide.Bottom => ScreenSide.Top,
        _ => throw new ArgumentOutOfRangeException(nameof(side), side, "Lado inválido.")
    };

    /// <summary>
    /// true se o ponto (x,y) está encostado na borda <paramref name="side"/> de <paramref name="bounds"/>
    /// (Right: x >= bounds.Right; Left: x <= bounds.X; Top: y <= bounds.Y; Bottom: y >= bounds.Bottom).
    /// </summary>
    public static bool IsAtEdge(ScreenBounds bounds, ScreenSide side, int x, int y) => side switch
    {
        ScreenSide.Right => x >= bounds.Right,
        ScreenSide.Left => x <= bounds.X,
        ScreenSide.Top => y <= bounds.Y,
        ScreenSide.Bottom => y >= bounds.Bottom,
        _ => throw new ArgumentOutOfRangeException(nameof(side), side, "Lado inválido.")
    };

    /// <summary>
    /// Posição normalizada 0..1 do ponto ao longo da borda <paramref name="side"/>
    /// (bordas Left/Right: (y - bounds.Y) / (Height-1); bordas Top/Bottom: (x - bounds.X) / (Width-1)). Resultado limitado a [0,1].
    /// </summary>
    public static double PositionAlongEdge(ScreenBounds bounds, ScreenSide side, int x, int y)
    {
        double position = side is ScreenSide.Left or ScreenSide.Right
            ? (double)(y - bounds.Y) / Math.Max(1, bounds.Height - 1)
            : (double)(x - bounds.X) / Math.Max(1, bounds.Width - 1);
        return Math.Clamp(position, 0.0, 1.0);
    }

    /// <summary>
    /// Ponto na borda <paramref name="side"/> correspondente a <paramref name="position"/> (0..1),
    /// deslocado <paramref name="inset"/> pixels para dentro da tela (para não disparar a troca de novo na hora).
    /// </summary>
    public static (int X, int Y) PointOnEdge(ScreenBounds bounds, ScreenSide side, double position, int inset)
    {
        double p = double.IsNaN(position) ? 0.0 : Math.Clamp(position, 0.0, 1.0);
        int alongY = bounds.Y + (int)Math.Round(p * Math.Max(0, bounds.Height - 1));
        int alongX = bounds.X + (int)Math.Round(p * Math.Max(0, bounds.Width - 1));

        return side switch
        {
            ScreenSide.Left => (bounds.X + inset, alongY),
            ScreenSide.Right => (bounds.Right - inset, alongY),
            ScreenSide.Top => (alongX, bounds.Y + inset),
            ScreenSide.Bottom => (alongX, bounds.Bottom - inset),
            _ => throw new ArgumentOutOfRangeException(nameof(side), side, "Lado inválido.")
        };
    }
}
