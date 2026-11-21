using ConnectDesktop.Core.Layout;
using ConnectDesktop.Core.Platform;
using Xunit;

namespace ConnectDesktop.Core.Tests;

public sealed class ScreenLayoutTests
{
    private static readonly ScreenBounds Single = new(0, 0, 1920, 1080);

    [Theory]
    [InlineData(ScreenSide.Left, ScreenSide.Right)]
    [InlineData(ScreenSide.Right, ScreenSide.Left)]
    [InlineData(ScreenSide.Top, ScreenSide.Bottom)]
    [InlineData(ScreenSide.Bottom, ScreenSide.Top)]
    public void Opposite_retorna_o_lado_oposto(ScreenSide side, ScreenSide expected)
    {
        Assert.Equal(expected, ScreenLayout.Opposite(side));
        Assert.Equal(side, ScreenLayout.Opposite(expected));
    }

    [Fact]
    public void IsAtEdge_Right_so_quando_x_encosta_na_borda_direita()
    {
        Assert.True(ScreenLayout.IsAtEdge(Single, ScreenSide.Right, 1919, 500));
        Assert.True(ScreenLayout.IsAtEdge(Single, ScreenSide.Right, 2000, 500));
        Assert.False(ScreenLayout.IsAtEdge(Single, ScreenSide.Right, 1918, 500));
    }

    [Fact]
    public void IsAtEdge_Left_so_quando_x_encosta_na_borda_esquerda()
    {
        Assert.True(ScreenLayout.IsAtEdge(Single, ScreenSide.Left, 0, 500));
        Assert.True(ScreenLayout.IsAtEdge(Single, ScreenSide.Left, -10, 500));
        Assert.False(ScreenLayout.IsAtEdge(Single, ScreenSide.Left, 1, 500));
    }

    [Fact]
    public void IsAtEdge_Top_so_quando_y_encosta_no_topo()
    {
        Assert.True(ScreenLayout.IsAtEdge(Single, ScreenSide.Top, 500, 0));
        Assert.True(ScreenLayout.IsAtEdge(Single, ScreenSide.Top, 500, -3));
        Assert.False(ScreenLayout.IsAtEdge(Single, ScreenSide.Top, 500, 1));
    }

    [Fact]
    public void IsAtEdge_Bottom_so_quando_y_encosta_na_base()
    {
        Assert.True(ScreenLayout.IsAtEdge(Single, ScreenSide.Bottom, 500, 1079));
        Assert.True(ScreenLayout.IsAtEdge(Single, ScreenSide.Bottom, 500, 2000));
        Assert.False(ScreenLayout.IsAtEdge(Single, ScreenSide.Bottom, 500, 1078));
    }

    [Fact]
    public void PositionAlongEdge_Right_e_Left_usam_y_normalizado()
    {
        Assert.Equal(0.0, ScreenLayout.PositionAlongEdge(Single, ScreenSide.Right, 1919, 0), 6);
        Assert.Equal(1.0, ScreenLayout.PositionAlongEdge(Single, ScreenSide.Right, 1919, 1079), 6);
        Assert.Equal(540.0 / 1079.0, ScreenLayout.PositionAlongEdge(Single, ScreenSide.Left, 0, 540), 6);
        Assert.Equal(0.5, ScreenLayout.PositionAlongEdge(Single, ScreenSide.Left, 0, 540), 0.001);
    }

    [Fact]
    public void PositionAlongEdge_Top_e_Bottom_usam_x_normalizado()
    {
        Assert.Equal(0.0, ScreenLayout.PositionAlongEdge(Single, ScreenSide.Top, 0, 540), 6);
        Assert.Equal(1.0, ScreenLayout.PositionAlongEdge(Single, ScreenSide.Bottom, 1919, 540), 6);
        Assert.Equal(960.0 / 1919.0, ScreenLayout.PositionAlongEdge(Single, ScreenSide.Top, 960, 0), 6);
    }

    [Fact]
    public void PositionAlongEdge_fica_limitado_entre_0_e_1()
    {
        Assert.Equal(0.0, ScreenLayout.PositionAlongEdge(Single, ScreenSide.Right, 1919, -50));
        Assert.Equal(1.0, ScreenLayout.PositionAlongEdge(Single, ScreenSide.Right, 1919, 5000));
        Assert.Equal(0.0, ScreenLayout.PositionAlongEdge(Single, ScreenSide.Top, -400, 0));
        Assert.Equal(1.0, ScreenLayout.PositionAlongEdge(Single, ScreenSide.Bottom, 9000, 0));
    }

    [Fact]
    public void PointOnEdge_Right_fica_inset_para_dentro()
    {
        var p0 = ScreenLayout.PointOnEdge(Single, ScreenSide.Right, 0.0, inset: 2);
        Assert.Equal(1917, p0.X);
        Assert.Equal(0, p0.Y);

        var p1 = ScreenLayout.PointOnEdge(Single, ScreenSide.Right, 1.0, inset: 2);
        Assert.Equal(1917, p1.X);
        Assert.Equal(1079, p1.Y);

        var mid = ScreenLayout.PointOnEdge(Single, ScreenSide.Right, 0.5, inset: 2);
        Assert.Equal(1917, mid.X);
        Assert.InRange(mid.Y, 539, 540);
    }

    [Fact]
    public void PointOnEdge_Left_Top_e_Bottom_com_inset()
    {
        var left = ScreenLayout.PointOnEdge(Single, ScreenSide.Left, 0.5, inset: 2);
        Assert.Equal(2, left.X);
        Assert.InRange(left.Y, 539, 540);

        var top = ScreenLayout.PointOnEdge(Single, ScreenSide.Top, 0.5, inset: 2);
        Assert.Equal(2, top.Y);
        Assert.InRange(top.X, 958, 960);

        var bottom = ScreenLayout.PointOnEdge(Single, ScreenSide.Bottom, 1.0, inset: 2);
        Assert.Equal(1077, bottom.Y);
        Assert.Equal(1919, bottom.X);
    }

    [Fact]
    public void PointOnEdge_sem_inset_fica_exatamente_na_borda()
    {
        Assert.Equal(1919, ScreenLayout.PointOnEdge(Single, ScreenSide.Right, 0.5, inset: 0).X);
        Assert.Equal(0, ScreenLayout.PointOnEdge(Single, ScreenSide.Left, 0.5, inset: 0).X);
    }

    [Fact]
    public void Bounds_com_X_e_Y_negativos_multi_monitor()
    {
        // Monitor principal 1920x1080 em (0,0) e monitor secundario 1280x1024 à esquerda, um pouco acima.
        // Área virtual: X de -1280 a 1919, Y de -200 a 1079 (altura 1280 para simplificar).
        var virtualDesktop = new ScreenBounds(-1280, -200, 3200, 1280);
        Assert.Equal(1919, virtualDesktop.Right);
        Assert.Equal(1079, virtualDesktop.Bottom);

        Assert.True(ScreenLayout.IsAtEdge(virtualDesktop, ScreenSide.Left, -1280, 0));
        Assert.False(ScreenLayout.IsAtEdge(virtualDesktop, ScreenSide.Left, -1279, 0));
        Assert.True(ScreenLayout.IsAtEdge(virtualDesktop, ScreenSide.Top, 0, -200));
        Assert.True(ScreenLayout.IsAtEdge(virtualDesktop, ScreenSide.Right, 1919, 0));
        Assert.True(ScreenLayout.IsAtEdge(virtualDesktop, ScreenSide.Bottom, 0, 1079));

        Assert.Equal(0.0, ScreenLayout.PositionAlongEdge(virtualDesktop, ScreenSide.Left, -1280, -200), 6);
        Assert.Equal(1.0, ScreenLayout.PositionAlongEdge(virtualDesktop, ScreenSide.Left, -1280, 1079), 6);
        Assert.Equal(0.0, ScreenLayout.PositionAlongEdge(virtualDesktop, ScreenSide.Top, -1280, -200), 6);

        var leftPoint = ScreenLayout.PointOnEdge(virtualDesktop, ScreenSide.Left, 0.0, inset: 2);
        Assert.Equal(-1278, leftPoint.X);
        Assert.Equal(-200, leftPoint.Y);

        var topPoint = ScreenLayout.PointOnEdge(virtualDesktop, ScreenSide.Top, 1.0, inset: 2);
        Assert.Equal(-198, topPoint.Y);
        Assert.Equal(1919, topPoint.X);
    }
}
