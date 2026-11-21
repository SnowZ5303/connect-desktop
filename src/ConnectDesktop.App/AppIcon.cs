using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace ConnectDesktop.App;

/// <summary>Ícone do app gerado em código: dois monitores lado a lado com uma seta, em #2563EB.</summary>
internal static class AppIcon
{
    private const string Hex = "#2563EB";

    public static WindowIcon Create()
    {
        var brush = new SolidColorBrush(Color.Parse(Hex));
        var canvas = new Canvas { Width = 64, Height = 64 };

        AddMonitor(canvas, 2, brush);
        AddMonitor(canvas, 38, brush);

        canvas.Children.Add(new Line
        {
            StartPoint = new Point(27, 23),
            EndPoint = new Point(34, 23),
            Stroke = brush,
            StrokeThickness = 3
        });

        var head = new Polygon { Fill = brush };
        head.Points.Add(new Point(30, 17));
        head.Points.Add(new Point(38, 23));
        head.Points.Add(new Point(30, 29));
        canvas.Children.Add(head);

        canvas.Measure(new Size(64, 64));
        canvas.Arrange(new Rect(0, 0, 64, 64));

        var bitmap = new RenderTargetBitmap(new PixelSize(64, 64), new Vector(96, 96));
        bitmap.Render(canvas);
        return new WindowIcon(bitmap);
    }

    private static void AddMonitor(Canvas canvas, double left, IBrush brush)
    {
        var screen = new Rectangle
        {
            Width = 24,
            Height = 18,
            Stroke = brush,
            StrokeThickness = 3,
            RadiusX = 2,
            RadiusY = 2
        };
        Canvas.SetLeft(screen, left);
        Canvas.SetTop(screen, 14);
        canvas.Children.Add(screen);
    }
}
