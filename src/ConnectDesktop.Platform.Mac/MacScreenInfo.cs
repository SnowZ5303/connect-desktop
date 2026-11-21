// Dono: g04. Informações de tela e cursor no macOS (CoreGraphics, coordenadas globais em pontos).
using ConnectDesktop.Core.Platform;

namespace ConnectDesktop.Platform.Mac;

public sealed class MacScreenInfo : IScreenInfo
{
    private const int MaxDisplays = 16;

    /// <summary>União de todos os monitores ativos. Coordenadas em pontos (CGDisplayBounds).</summary>
    public ScreenBounds GetDesktopBounds()
    {
        uint[] ids = new uint[MaxDisplays];
        uint count;
        if (MacNative.CGGetActiveDisplayList(MaxDisplays, ids, out count) != 0 || count == 0)
        {
            ids[0] = MacNative.CGMainDisplayID();
            count = 1;
        }

        double minX = double.MaxValue;
        double minY = double.MaxValue;
        double maxX = double.MinValue;
        double maxY = double.MinValue;
        int n = (int)Math.Min(count, (uint)MaxDisplays);
        for (int i = 0; i < n; i++)
        {
            CGRect r = MacNative.CGDisplayBounds(ids[i]);
            minX = Math.Min(minX, r.Origin.X);
            minY = Math.Min(minY, r.Origin.Y);
            maxX = Math.Max(maxX, r.Origin.X + r.Size.Width);
            maxY = Math.Max(maxY, r.Origin.Y + r.Size.Height);
        }

        int x = (int)Math.Round(minX);
        int y = (int)Math.Round(minY);
        int width = Math.Max(1, (int)Math.Round(maxX - minX));
        int height = Math.Max(1, (int)Math.Round(maxY - minY));
        return new ScreenBounds(x, y, width, height);
    }

    public (int X, int Y) GetCursorPosition()
    {
        CGPoint p = MacNative.GetCursorLocation();
        return ((int)Math.Round(p.X), (int)Math.Round(p.Y));
    }

    public void SetCursorPosition(int x, int y)
    {
        MacNative.CGWarpMouseCursorPosition(new CGPoint(x, y));
        MacNative.CGAssociateMouseAndMouseCursorPosition(true);
    }
}
