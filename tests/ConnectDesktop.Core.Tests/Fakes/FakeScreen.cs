using ConnectDesktop.Core.Platform;

namespace ConnectDesktop.Core.Tests.Fakes;

/// <summary>Tela falsa com bounds configuráveis e registro de SetCursorPosition.</summary>
public sealed class FakeScreen : IScreenInfo
{
    private readonly object _lock = new();
    private readonly List<(int X, int Y)> _setCursorCalls = new();
    private ScreenBounds _bounds;
    private (int X, int Y) _cursor;

    public FakeScreen(ScreenBounds? bounds = null)
    {
        _bounds = bounds ?? new ScreenBounds(0, 0, 1920, 1080);
        _cursor = (_bounds.X, _bounds.Y);
    }

    public ScreenBounds Bounds
    {
        get { lock (_lock) return _bounds; }
        set { lock (_lock) _bounds = value; }
    }

    public (int X, int Y) Cursor
    {
        get { lock (_lock) return _cursor; }
        set { lock (_lock) _cursor = value; }
    }

    /// <summary>Todas as chamadas a SetCursorPosition, na ordem.</summary>
    public IReadOnlyList<(int X, int Y)> SetCursorCalls
    {
        get { lock (_lock) return _setCursorCalls.ToArray(); }
    }

    public ScreenBounds GetDesktopBounds() => Bounds;

    public (int X, int Y) GetCursorPosition() => Cursor;

    public void SetCursorPosition(int x, int y)
    {
        lock (_lock)
        {
            _cursor = (x, y);
            _setCursorCalls.Add((x, y));
        }
    }
}
