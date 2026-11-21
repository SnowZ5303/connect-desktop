// Dono: g15. API pública FIXA (usada pelo g16). Só Windows nesta versão.
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Threading;
using ConnectDesktop.App.Views;
using ConnectDesktop.Core.Layout;

namespace ConnectDesktop.App.Services;

/// <summary>
/// Vigia o mouse e mostra a faixa de "soltar" (EdgeDropWindow) na borda da tela onde fica o outro PC,
/// quando o botão esquerdo está pressionado a até 60 px dessa borda. Soltar arquivos, pastas, links ou texto
/// nela dispara os eventos. Só Windows nesta versão: em outros SOs, Start não faz nada.
/// Start e Stop devem ser chamados na UI thread.
/// </summary>
public sealed class EdgeDropController : IDisposable
{
    private const int ActivationDistance = 60;
    private const int HideDistance = 200;
    private const int StripThickness = 14;
    private const int VkLButton = 0x01;
    private const uint MonitorDefaultToNearest = 2;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);
    private const int DragThresholdPx = 10;
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
    private static readonly TimeSpan PostDropDelay = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan DragOverTimeout = TimeSpan.FromSeconds(2);

    private readonly Func<ScreenSide?> _getEdge;
    private DispatcherTimer? _timer;
    private EdgeDropWindow? _window;
    private bool _visible;
    private bool _dragOver;
    private bool _disposed;
    private bool _tickErrorLogged;
    private NativeRect _shownBounds;
    private ScreenSide _shownSide;
    private DateTime? _hideAt;
    private POINT? _pressPoint;
    private DateTime? _releasedAt;

    /// <param name="getEdge">Lado da tela onde fica o outro PC agora (null = sem conexão: não mostra nada). Lido a cada verificação.</param>
    public EdgeDropController(Func<ScreenSide?> getEdge)
    {
        _getEdge = getEdge ?? throw new ArgumentNullException(nameof(getEdge));
    }

    /// <summary>Arquivos e/ou pastas soltos (caminhos locais completos). Na UI thread.</summary>
    public event Action<IReadOnlyList<string>>? PathsDropped;

    /// <summary>Link solto (http/https/mailto, absoluto). Na UI thread.</summary>
    public event Action<string>? UrlDropped;

    /// <summary>Texto solto que não é link. Na UI thread.</summary>
    public event Action<string>? TextDropped;

    /// <summary>Começa a vigiar (só faz algo no Windows).</summary>
    public void Start()
    {
        if (_disposed || !OperatingSystem.IsWindows() || _timer is not null)
            return;

        _timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = PollInterval };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    /// <summary>Para de vigiar e esconde a faixa.</summary>
    public void Stop()
    {
        if (_timer is not null)
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
            _timer = null;
        }

        _dragOver = false;
        _hideAt = null;
        HideStrip();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        Stop();
        _disposed = true;
        _window?.Close();
        _window = null;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        try
        {
            Check();
        }
        catch (Exception ex)
        {
            if (!_tickErrorLogged)
            {
                _tickErrorLogged = true;
                Log.Error("EdgeDrop: falha ao verificar o mouse.", ex);
            }
        }
    }

    private void Check()
    {
        var edge = _getEdge();
        if (edge is null)
        {
            _dragOver = false;
            _hideAt = null;
            _pressPoint = null;
            _releasedAt = null;
            HideStrip();
            return;
        }

        // Depois de um drop, a faixa some 300 ms depois e não reaparece nesse meio-tempo.
        if (_hideAt is { } hideAt)
        {
            if (DateTime.UtcNow < hideAt)
                return;

            _hideAt = null;
            HideStrip();
            return;
        }

        if (!GetCursorPos(out var cursor))
            return;

        if (!IsLeftButtonDown())
        {
            _pressPoint = null;

            // Soltou o botão: esconde, exceto enquanto o OLE ainda entrega o drop na faixa (o Drop/DragLeave encerra).
            // Segurança: se _dragOver continuar ligado mais de 2 s depois de soltar, zera e esconde.
            if (_dragOver)
            {
                _releasedAt ??= DateTime.UtcNow;
                if (DateTime.UtcNow - _releasedAt.Value > DragOverTimeout)
                {
                    _dragOver = false;
                    _releasedAt = null;
                    HideStrip();
                }
                return;
            }

            _releasedAt = null;
            HideStrip();
            return;
        }

        _releasedAt = null;
        _pressPoint ??= cursor;

        // Só vale depois de um arrasto de verdade: o cursor precisa ter andado pelo menos 10 px desde o botão pressionado.
        var press = _pressPoint.Value;
        if (!HasDragged(press.X, press.Y, cursor.X, cursor.Y))
            return;

        if (TryGetMonitorEdge(cursor, edge.Value, out var bounds, out var distance) && distance <= ActivationDistance)
        {
            ShowStrip(bounds, edge.Value, cursor);
            return;
        }

        if (_visible && (!Contains(_shownBounds, cursor) || DistanceTo(cursor, _shownBounds, _shownSide) > HideDistance))
            HideStrip();
    }

    private static bool Contains(NativeRect rect, POINT p)
        => p.X >= rect.Left && p.X < rect.Right && p.Y >= rect.Top && p.Y < rect.Bottom;

    private void ShowStrip(NativeRect bounds, ScreenSide side, POINT cursor)
    {
        if (_visible && bounds == _shownBounds && side == _shownSide)
            return;

        _window ??= CreateWindow();

        // Espessura de 14 px lógicos (em físicos conforme a escala da tela); comprimento = lado do monitor.
        var scaling = _window.Screens?.ScreenFromPoint(new PixelPoint(cursor.X, cursor.Y))?.Scaling ?? 1.0;
        var thickness = Math.Max(1, (int)Math.Round(StripThickness * scaling));
        var vertical = side is ScreenSide.Left or ScreenSide.Right;

        int x, y, length;
        switch (side)
        {
            case ScreenSide.Left:
                x = bounds.Left; y = bounds.Top; length = bounds.Height;
                break;
            case ScreenSide.Right:
                x = bounds.Right - thickness; y = bounds.Top; length = bounds.Height;
                break;
            case ScreenSide.Top:
                x = bounds.Left; y = bounds.Top; length = bounds.Width;
                break;
            default:
                x = bounds.Left; y = bounds.Bottom - thickness; length = bounds.Width;
                break;
        }

        _window.SetVertical(vertical);
        _window.Position = new PixelPoint(x, y);
        _window.Width = vertical ? StripThickness : length / scaling;
        _window.Height = vertical ? length / scaling : StripThickness;

        if (!_visible)
        {
            _window.Show();
            _visible = true;
        }

        _window.PlaceStrip(x, y, thickness, length, vertical);
        _shownBounds = bounds;
        _shownSide = side;
    }

    private void HideStrip()
    {
        if (!_visible)
            return;

        _visible = false;
        _window?.Hide();
    }

    private EdgeDropWindow CreateWindow()
    {
        var window = new EdgeDropWindow();
        window.DragEntered += () => _dragOver = true;
        window.DragLeft += () => _dragOver = false;
        window.PathsDropped += paths => PathsDropped?.Invoke(paths);
        window.UrlDropped += url => UrlDropped?.Invoke(url);
        window.TextDropped += text => TextDropped?.Invoke(text);
        window.DropFinished += () =>
        {
            _dragOver = false;
            _hideAt = DateTime.UtcNow + PostDropDelay;
        };
        return window;
    }

    /// <summary>
    /// Monitor sob o cursor (área completa) e a distância do cursor até a borda pedida.
    /// Retorna false se a borda não for externa ao desktop virtual (emenda entre monitores).
    /// </summary>
    private static bool TryGetMonitorEdge(POINT cursor, ScreenSide side, out NativeRect bounds, out int distance)
    {
        bounds = default;
        distance = int.MaxValue;

        var monitor = MonitorFromPoint(cursor, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
            return false;

        var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfoW(monitor, ref info))
            return false;

        bounds = NativeRect.From(info.rcMonitor);
        var virtualDesktop = VirtualDesktop();
        if (!IsExternalEdge(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom,
                virtualDesktop.Left, virtualDesktop.Top, virtualDesktop.Right, virtualDesktop.Bottom, side))
            return false;

        distance = DistanceTo(cursor, bounds, side);
        return true;
    }

    /// <summary>Desktop virtual (todos os monitores somados), em pixels físicos.</summary>
    private static NativeRect VirtualDesktop()
    {
        var left = GetSystemMetrics(SmXVirtualScreen);
        var top = GetSystemMetrics(SmYVirtualScreen);
        return new NativeRect(left, top, left + GetSystemMetrics(SmCxVirtualScreen), top + GetSystemMetrics(SmCyVirtualScreen));
    }

    /// <summary>
    /// true se a borda do monitor coincide com a borda do desktop virtual do lado pedido, ou seja, a borda é externa
    /// (não há outro monitor encostado nela). Função pura: testada no scratch.
    /// </summary>
    internal static bool IsExternalEdge(int left, int top, int right, int bottom,
        int virtualLeft, int virtualTop, int virtualRight, int virtualBottom, ScreenSide side) => side switch
    {
        ScreenSide.Left => left == virtualLeft,
        ScreenSide.Right => right == virtualRight,
        ScreenSide.Top => top == virtualTop,
        _ => bottom == virtualBottom,
    };

    /// <summary>true se o cursor andou pelo menos DragThresholdPx (distância euclidiana) desde o ponto onde o botão desceu. Função pura.</summary>
    internal static bool HasDragged(int fromX, int fromY, int x, int y)
    {
        var dx = (long)x - fromX;
        var dy = (long)y - fromY;
        return dx * dx + dy * dy >= (long)DragThresholdPx * DragThresholdPx;
    }

    private static int DistanceTo(POINT p, NativeRect r, ScreenSide side) => side switch
    {
        ScreenSide.Left => p.X - r.Left,
        ScreenSide.Right => r.Right - 1 - p.X,
        ScreenSide.Top => p.Y - r.Top,
        _ => r.Bottom - 1 - p.Y,
    };

    private static bool IsLeftButtonDown() => (GetAsyncKeyState(VkLButton) & 0x8000) != 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    /// <summary>Retângulo em pixels físicos.</summary>
    private readonly record struct NativeRect(int Left, int Top, int Right, int Bottom)
    {
        public int Width => Right - Left;
        public int Height => Bottom - Top;

        public static NativeRect From(RECT r) => new(r.Left, r.Top, r.Right, r.Bottom);
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);
}
