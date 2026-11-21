// CONTRATO FIXO (escrito pelo main). Geradores: NÃO editar este arquivo.
// Se precisar mudar algo aqui, pergunte ao @main na sala.
namespace ConnectDesktop.Core.Platform;

/// <summary>Botões do mouse, independentes de sistema.</summary>
public enum MouseButton : byte { Left = 1, Right = 2, Middle = 3, X1 = 4, X2 = 5 }

/// <summary>Retângulo da área de trabalho em pixels (coordenadas do sistema).</summary>
public readonly record struct ScreenBounds(int X, int Y, int Width, int Height)
{
    public int Right => X + Width - 1;
    public int Bottom => Y + Height - 1;
}

/// <summary>
/// Movimento do mouse capturado no PC principal.
/// X/Y = posição absoluta que o cursor teria (coordenadas da área de trabalho local).
/// DX/DY = deslocamento relativo desde o último evento (em pixels).
/// Quando <see cref="IInputCapture.SuppressLocalInput"/> está ligado, o cursor local fica parado,
/// mas DX/DY continuam refletindo o movimento físico do mouse.
/// </summary>
public readonly record struct MouseMoveEvent(int X, int Y, int DX, int DY);

public readonly record struct MouseButtonEvent(MouseButton Button, bool Down);

/// <summary>Roda do mouse. Unidades: 120 = um "clique" de roda (padrão WHEEL_DELTA). DY positivo = rolar para cima.</summary>
public readonly record struct MouseWheelEvent(int DX, int DY);

/// <summary>
/// Tecla. <see cref="Hid"/> = código de uso HID (USB HID Usage Tables, página 0x07 "Keyboard/Keypad").
/// Ex.: A=0x04, Enter=0x28, Esc=0x29, Espaço=0x2C, LeftCtrl=0xE0, LeftShift=0xE1, LeftAlt=0xE2, LeftGUI=0xE3,
/// RightCtrl=0xE4, RightShift=0xE5, RightAlt=0xE6, RightGUI=0xE7. Cada plataforma converte de/para o código nativo.
/// </summary>
public readonly record struct KeyEvent(ushort Hid, bool Down);

/// <summary>Captura global de mouse e teclado (usada no PC principal).</summary>
public interface IInputCapture : IDisposable
{
    /// <summary>Os eventos podem ser disparados numa thread do hook. Os assinantes devem ser rápidos e thread-safe.</summary>
    event Action<MouseMoveEvent>? MouseMoved;
    event Action<MouseButtonEvent>? MouseButtonChanged;
    event Action<MouseWheelEvent>? MouseWheel;
    event Action<KeyEvent>? KeyChanged;

    /// <summary>
    /// true = os eventos são "engolidos" (não chegam aos programas locais) e o cursor local fica parado
    /// (a implementação pode estacioná-lo num ponto fixo e calcular DX/DY a partir dele).
    /// false = os eventos passam normalmente; a captura só observa.
    /// </summary>
    bool SuppressLocalInput { get; set; }

    /// <summary>Inicia a captura (cria hooks/threads). Lança <see cref="PlatformNotSupportedException"/> ou InvalidOperationException com mensagem clara se faltar permissão.</summary>
    void Start();
    void Stop();
}

/// <summary>Injeção de mouse e teclado (usada no PC secundário).</summary>
public interface IInputInjector
{
    void MoveMouseAbsolute(int x, int y);
    void MouseButton(MouseButton button, bool down);
    /// <summary>Mesmas unidades de <see cref="MouseWheelEvent"/>.</summary>
    void MouseWheel(int dx, int dy);
    void Key(ushort hid, bool down);
}

public interface IScreenInfo
{
    /// <summary>Retângulo que envolve todos os monitores (área de trabalho virtual).</summary>
    ScreenBounds GetDesktopBounds();
    (int X, int Y) GetCursorPosition();
    /// <summary>Move o cursor local sem gerar evento de captura relevante (usado ao voltar do PC remoto).</summary>
    void SetCursorPosition(int x, int y);
}

/// <summary>Área de transferência (só texto nesta versão).</summary>
public interface IClipboard
{
    /// <summary>Retorna null se não houver texto. Nunca lança: em erro retorna null.</summary>
    string? GetText();
    /// <summary>Nunca lança: em erro ignora.</summary>
    void SetText(string text);
}

/// <summary>Ponto de entrada de cada plataforma (Windows, Mac, Linux).</summary>
public interface IPlatform
{
    string Name { get; }
    IScreenInfo Screen { get; }
    IClipboard Clipboard { get; }
    IInputCapture CreateCapture();
    IInputInjector CreateInjector();

    /// <summary>
    /// Confere permissões do sistema (ex.: Acessibilidade no Mac, X11 no Linux).
    /// Retorna true se tudo ok; senão false e <paramref name="message"/> explica o que o usuário deve fazer (em português).
    /// </summary>
    bool CheckPermissions(out string message);
}

/// <summary>Códigos HID usados pelo núcleo (atalhos).</summary>
public static class HidKeys
{
    public const ushort Escape = 0x29;
    public const ushort LeftCtrl = 0xE0, LeftShift = 0xE1, LeftAlt = 0xE2, LeftGui = 0xE3;
    public const ushort RightCtrl = 0xE4, RightShift = 0xE5, RightAlt = 0xE6, RightGui = 0xE7;
}
