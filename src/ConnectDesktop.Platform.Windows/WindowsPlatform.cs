using ConnectDesktop.Core.Platform;

namespace ConnectDesktop.Platform.Windows;

/// <summary>
/// Implementação de <see cref="IPlatform"/> para Windows (net8.0, somente P/Invoke em user32 e kernel32).
/// Limitação: janelas de administrador não recebem input injetado, a menos que o app rode como administrador.
/// </summary>
public sealed class WindowsPlatform : IPlatform
{
    private readonly WindowsScreenInfo _screen = new();
    private readonly WindowsClipboard _clipboard = new();

    /// <summary>Cria a plataforma Windows.</summary>
    public WindowsPlatform()
    {
    }

    /// <inheritdoc />
    public string Name => "Windows";

    /// <inheritdoc />
    public IScreenInfo Screen => _screen;

    /// <inheritdoc />
    public IClipboard Clipboard => _clipboard;

    /// <inheritdoc />
    public IInputCapture CreateCapture() => new WindowsInputCapture();

    /// <inheritdoc />
    public IInputInjector CreateInjector() => new WindowsInputInjector();

    /// <summary>
    /// Não há permissão especial a conferir no Windows. Se o processo não for administrador,
    /// janelas de administrador não receberão o input injetado; nesse caso a mensagem continua vazia.
    /// </summary>
    public bool CheckPermissions(out string message)
    {
        message = string.Empty;
        return true;
    }
}
