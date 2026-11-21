using ConnectDesktop.Core.Platform;

namespace ConnectDesktop.Core.Tests.Fakes;

/// <summary>Captura de entrada falsa: os testes disparam os eventos pelos métodos Move/Button/Wheel/Key.</summary>
public sealed class FakeCapture : IInputCapture
{
    private readonly object _lock = new();
    private readonly List<bool> _suppressHistory = new();
    private bool _suppress;

    public event Action<MouseMoveEvent>? MouseMoved;
    public event Action<MouseButtonEvent>? MouseButtonChanged;
    public event Action<MouseWheelEvent>? MouseWheel;
    public event Action<KeyEvent>? KeyChanged;

    public bool SuppressLocalInput
    {
        get { lock (_lock) return _suppress; }
        set
        {
            lock (_lock)
            {
                _suppress = value;
                _suppressHistory.Add(value);
            }
        }
    }

    /// <summary>Todos os valores atribuídos a SuppressLocalInput, na ordem.</summary>
    public IReadOnlyList<bool> SuppressLocalInputHistory
    {
        get { lock (_lock) return _suppressHistory.ToArray(); }
    }

    public bool Started { get; private set; }
    public bool Stopped { get; private set; }

    public void Start() => Started = true;
    public void Stop() => Stopped = true;

    public void Move(int x, int y, int dx, int dy) => MouseMoved?.Invoke(new MouseMoveEvent(x, y, dx, dy));
    public void Button(MouseButton button, bool down) => MouseButtonChanged?.Invoke(new MouseButtonEvent(button, down));
    public void Wheel(int dx, int dy) => MouseWheel?.Invoke(new MouseWheelEvent(dx, dy));
    public void Key(ushort hid, bool down) => KeyChanged?.Invoke(new KeyEvent(hid, down));

    public void Dispose()
    {
    }
}
