using ConnectDesktop.Core.Platform;

namespace ConnectDesktop.Core.Tests.Fakes;

/// <summary>Chamada feita ao injetor falso.</summary>
public abstract record InjectedAction;
public sealed record MoveAbsoluteAction(int X, int Y) : InjectedAction;
public sealed record MouseButtonAction(MouseButton Button, bool Down) : InjectedAction;
public sealed record MouseWheelAction(int DX, int DY) : InjectedAction;
public sealed record KeyAction(ushort Hid, bool Down) : InjectedAction;

/// <summary>Injetor falso: registra todas as chamadas, em ordem, de forma thread-safe.</summary>
public sealed class FakeInjector : IInputInjector
{
    private readonly object _lock = new();
    private readonly List<InjectedAction> _actions = new();

    /// <summary>Cópia da lista de chamadas, na ordem em que aconteceram.</summary>
    public IReadOnlyList<InjectedAction> Actions
    {
        get { lock (_lock) return _actions.ToArray(); }
    }

    private void Add(InjectedAction action)
    {
        lock (_lock) _actions.Add(action);
    }

    public void MoveMouseAbsolute(int x, int y) => Add(new MoveAbsoluteAction(x, y));
    public void MouseButton(MouseButton button, bool down) => Add(new MouseButtonAction(button, down));
    public void MouseWheel(int dx, int dy) => Add(new MouseWheelAction(dx, dy));
    public void Key(ushort hid, bool down) => Add(new KeyAction(hid, down));
}
