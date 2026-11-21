// Dono: g04. Ponto de entrada da plataforma macOS. O construtor não executa código nativo.
using ConnectDesktop.Core.Platform;

namespace ConnectDesktop.Platform.Mac;

public sealed class MacPlatform : IPlatform
{
    private readonly MacScreenInfo _screen = new();
    private readonly MacClipboard _clipboard = new();

    public string Name => "macOS";

    public IScreenInfo Screen => _screen;

    public IClipboard Clipboard => _clipboard;

    public IInputCapture CreateCapture() => new MacInputCapture();

    public IInputInjector CreateInjector() => new MacInputInjector();

    public bool CheckPermissions(out string message)
    {
        return MacPermissions.Check(out message);
    }
}
