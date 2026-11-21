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
    FileOffer = 70,      // remetente -> destinatário: vai começar a enviar um arquivo
    FileChunk = 71,      // remetente -> destinatário: bloco de dados (até 64 KB)
    FileDone = 72,       // remetente -> destinatário: terminou; traz o SHA-256 do arquivo inteiro
    FileCancel = 73,     // qualquer lado: cancela a transferência
    FileResult = 74,     // destinatário -> remetente: resultado final (ok ou erro)
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

// ---- Transferência de arquivos (qualquer sentido) ----
// Codec: Guid = 16 bytes (Guid.TryWriteBytes); string = int32 tamanho em bytes + UTF-8; byte[] = int32 tamanho + bytes.

/// <summary>FileName = só o nome (sem pastas). Size em bytes.</summary>
public sealed record FileOfferMessage(Guid TransferId, string FileName, long Size) : Message(MessageType.FileOffer);
/// <summary>Data com no máximo <see cref="FileTransferLimits.ChunkSize"/> bytes. Offset = posição no arquivo.</summary>
public sealed record FileChunkMessage(Guid TransferId, long Offset, byte[] Data) : Message(MessageType.FileChunk);
/// <summary>Sha256 = 32 bytes do hash do arquivo inteiro.</summary>
public sealed record FileDoneMessage(Guid TransferId, byte[] Sha256) : Message(MessageType.FileDone);
public sealed record FileCancelMessage(Guid TransferId, string Reason) : Message(MessageType.FileCancel);
/// <summary>Error vazio quando Ok = true.</summary>
public sealed record FileResultMessage(Guid TransferId, bool Ok, string Error) : Message(MessageType.FileResult);

public static class FileTransferLimits
{
    public const int ChunkSize = 64 * 1024;
    public const int MaxFileNameBytes = 1024;
}
