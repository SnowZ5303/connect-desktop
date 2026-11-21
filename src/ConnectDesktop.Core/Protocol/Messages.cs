// CONTRATO FIXO (escrito pelo main). Geradores: NÃO editar este arquivo.
using ConnectDesktop.Core.Platform;

namespace ConnectDesktop.Core.Protocol;

public enum MessageType : byte
{
    MouseMove = 10,      // principal -> secundário: deslocamento relativo
    MouseButton = 11,    // principal -> secundário
    MouseWheel = 12,     // principal -> secundário
    Key = 13,            // principal -> secundário
    Enter = 20,          // principal -> secundário: o cursor entrou na sua tela
    Leave = 21,          // secundário -> principal: o cursor saiu de volta para o principal
    ReleaseAll = 22,     // principal -> secundário: solte todas as teclas/botões pressionados
    Clipboard = 30,      // ambos os sentidos: texto UTF-8
    ScreenInfo = 50,     // secundário -> principal, logo após conectar
    Ping = 60,
    Pong = 61,
}

/// <summary>Mensagem de aplicação, trafega dentro do <see cref="Net.IMessageChannel"/>.</summary>
public abstract record Message(MessageType Type);

public sealed record MouseMoveMessage(int DX, int DY) : Message(MessageType.MouseMove);
public sealed record MouseButtonMessage(MouseButton Button, bool Down) : Message(MessageType.MouseButton);
public sealed record MouseWheelMessage(int DX, int DY) : Message(MessageType.MouseWheel);
public sealed record KeyMessage(ushort Hid, bool Down) : Message(MessageType.Key);

/// <summary>
/// PrimarySide = lado da tela do SECUNDÁRIO por onde o cursor entra, que é onde o principal fica
/// (= ScreenLayout.Opposite(lado configurado no principal)). O secundário usa esse lado para saber por onde sair.
/// Position = posição ao longo da borda de entrada, de 0.0 a 1.0
/// (borda vertical: 0 = topo, 1 = base; borda horizontal: 0 = esquerda, 1 = direita).
/// </summary>
public sealed record EnterMessage(Layout.ScreenSide PrimarySide, double Position) : Message(MessageType.Enter);
/// <summary>Mesma convenção de <see cref="EnterMessage.Position"/>, medida na borda por onde o cursor saiu.</summary>
public sealed record LeaveMessage(double Position) : Message(MessageType.Leave);
public sealed record ReleaseAllMessage() : Message(MessageType.ReleaseAll);
public sealed record ClipboardMessage(string Text) : Message(MessageType.Clipboard);
public sealed record ScreenInfoMessage(int Width, int Height) : Message(MessageType.ScreenInfo);
public sealed record PingMessage(long Timestamp) : Message(MessageType.Ping);
public sealed record PongMessage(long Timestamp) : Message(MessageType.Pong);
