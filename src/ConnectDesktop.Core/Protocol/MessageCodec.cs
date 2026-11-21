// Dono: g01. Assinaturas públicas FIXAS (não mudar nomes/parâmetros).
using System.Buffers.Binary;
using System.Text;
using ConnectDesktop.Core.Layout;
using ConnectDesktop.Core.Platform;

namespace ConnectDesktop.Core.Protocol;

/// <summary>
/// Serializa mensagens em bytes (sem cabeçalho de tamanho; o enquadramento é do canal).
/// Formato: [1 byte MessageType][payload little-endian].
/// MouseMove: int32 DX, int32 DY | MouseButton: byte Button, byte Down(0/1) | MouseWheel: int32 DX, int32 DY
/// Key: uint16 Hid, byte Down | Enter: byte PrimarySide, double Position | Leave: double Position | ReleaseAll: vazio
/// Clipboard: bytes UTF-8 (resto do buffer) | ScreenInfo: int32 W, int32 H | Ping/Pong: int64 Timestamp
/// </summary>
public static class MessageCodec
{
    /// <summary>Tamanho máximo de uma mensagem serializada (texto de clipboard incluso).</summary>
    public const int MaxMessageSize = 1024 * 1024;

    /// <summary>Limite do OpenUrl em bytes UTF-8 (8 KB).</summary>
    private const int MaxUrlBytes = 8 * 1024;
    /// <summary>Limite de Guids por DropBatch.</summary>
    private const int MaxDropBatchCount = 1000;

    private const int MaxMouseButtonValue = (int)MouseButton.X2;
    private const int MaxScreenSideValue = (int)ScreenSide.Bottom;

    /// <summary>Lança <see cref="ArgumentException"/> se a mensagem não couber em <see cref="MaxMessageSize"/>.</summary>
    public static byte[] Encode(Message message)
    {
        byte[] payload;
        switch (message)
        {
            case MouseMoveMessage m:
                payload = new byte[8];
                BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(0), m.DX);
                BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4), m.DY);
                break;
            case MouseButtonMessage m:
                payload = new byte[] { (byte)m.Button, m.Down ? (byte)1 : (byte)0 };
                break;
            case MouseWheelMessage m:
                payload = new byte[8];
                BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(0), m.DX);
                BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4), m.DY);
                break;
            case KeyMessage m:
                payload = new byte[3];
                BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0), m.Hid);
                payload[2] = m.Down ? (byte)1 : (byte)0;
                break;
            case EnterMessage m:
                payload = new byte[9];
                payload[0] = (byte)(int)m.PrimarySide;
                BinaryPrimitives.WriteDoubleLittleEndian(payload.AsSpan(1), m.Position);
                break;
            case LeaveMessage m:
                payload = new byte[8];
                BinaryPrimitives.WriteDoubleLittleEndian(payload.AsSpan(0), m.Position);
                break;
            case ReleaseAllMessage:
                payload = Array.Empty<byte>();
                break;
            case ClipboardMessage m:
                payload = Encoding.UTF8.GetBytes(m.Text);
                break;
            case ScreenInfoMessage m:
                payload = new byte[8];
                BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(0), m.Width);
                BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4), m.Height);
                break;
            case PingMessage m:
                payload = new byte[8];
                BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(0), m.Timestamp);
                break;
            case PongMessage m:
                payload = new byte[8];
                BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(0), m.Timestamp);
                break;
            case FileOfferMessage m:
                {
                    var name = Encoding.UTF8.GetBytes(m.FileName);
                    if (name.Length > FileTransferLimits.MaxFileNameBytes)
                        throw new ArgumentException("Nome de arquivo grande demais.", nameof(message));
                    payload = new byte[16 + 4 + name.Length + 8];
                    m.TransferId.TryWriteBytes(payload.AsSpan(0, 16));
                    BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(16), name.Length);
                    name.CopyTo(payload, 20);
                    BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(20 + name.Length), m.Size);
                    break;
                }
            case FileChunkMessage m:
                {
                    if (m.Data.Length > FileTransferLimits.ChunkSize)
                        throw new ArgumentException("Bloco de arquivo grande demais.", nameof(message));
                    payload = new byte[16 + 8 + 4 + m.Data.Length];
                    m.TransferId.TryWriteBytes(payload.AsSpan(0, 16));
                    BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(16), m.Offset);
                    BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(24), m.Data.Length);
                    m.Data.CopyTo(payload, 28);
                    break;
                }
            case FileDoneMessage m:
                {
                    if (m.Sha256.Length != 32)
                        throw new ArgumentException("SHA-256 deve ter 32 bytes.", nameof(message));
                    payload = new byte[16 + 4 + 32];
                    m.TransferId.TryWriteBytes(payload.AsSpan(0, 16));
                    BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(16), 32);
                    m.Sha256.CopyTo(payload, 20);
                    break;
                }
            case FileCancelMessage m:
                {
                    var reason = Encoding.UTF8.GetBytes(m.Reason);
                    payload = new byte[16 + 4 + reason.Length];
                    m.TransferId.TryWriteBytes(payload.AsSpan(0, 16));
                    BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(16), reason.Length);
                    reason.CopyTo(payload, 20);
                    break;
                }
            case FileResultMessage m:
                {
                    var error = Encoding.UTF8.GetBytes(m.Error);
                    payload = new byte[16 + 1 + 4 + error.Length];
                    m.TransferId.TryWriteBytes(payload.AsSpan(0, 16));
                    payload[16] = m.Ok ? (byte)1 : (byte)0;
                    BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(17), error.Length);
                    error.CopyTo(payload, 21);
                    break;
                }
            case OpenUrlMessage m:
                {
                    var url = Encoding.UTF8.GetBytes(m.Url);
                    if (url.Length > MaxUrlBytes)
                        throw new ArgumentException("Link grande demais.", nameof(message));
                    payload = new byte[4 + url.Length];
                    BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(0), url.Length);
                    url.CopyTo(payload, 4);
                    break;
                }
            case DropBatchMessage m:
                {
                    if (m.TransferIds.Length > MaxDropBatchCount)
                        throw new ArgumentException("Lote de arrastar grande demais.", nameof(message));
                    payload = new byte[4 + 16 * m.TransferIds.Length];
                    BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(0), m.TransferIds.Length);
                    for (var i = 0; i < m.TransferIds.Length; i++)
                        m.TransferIds[i].TryWriteBytes(payload.AsSpan(4 + 16 * i, 16));
                    break;
                }
            default:
                throw new ArgumentException("Tipo de mensagem desconhecido.", nameof(message));
        }

        if (1 + payload.Length > MaxMessageSize)
            throw new ArgumentException("Mensagem grande demais para enviar.", nameof(message));

        var result = new byte[1 + payload.Length];
        result[0] = (byte)message.Type;
        payload.CopyTo(result, 1);
        return result;
    }

    /// <summary>Lança <see cref="FormatException"/> se os bytes forem inválidos ou o tipo desconhecido.</summary>
    public static Message Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 1 || data.Length > MaxMessageSize)
            throw new FormatException("Mensagem vazia ou com tamanho inválido.");

        var type = data[0];
        var p = data.Slice(1);

        switch ((MessageType)type)
        {
            case MessageType.MouseMove:
                Expect(p, 8, MessageType.MouseMove);
                return new MouseMoveMessage(BinaryPrimitives.ReadInt32LittleEndian(p.Slice(0, 4)),
                                            BinaryPrimitives.ReadInt32LittleEndian(p.Slice(4, 4)));
            case MessageType.MouseButton:
                Expect(p, 2, MessageType.MouseButton);
                return new MouseButtonMessage((MouseButton)ReadMouseButton(p[0]), ReadBool(p[1]));
            case MessageType.MouseWheel:
                Expect(p, 8, MessageType.MouseWheel);
                return new MouseWheelMessage(BinaryPrimitives.ReadInt32LittleEndian(p.Slice(0, 4)),
                                             BinaryPrimitives.ReadInt32LittleEndian(p.Slice(4, 4)));
            case MessageType.Key:
                Expect(p, 3, MessageType.Key);
                return new KeyMessage(BinaryPrimitives.ReadUInt16LittleEndian(p.Slice(0, 2)), ReadBool(p[2]));
            case MessageType.Enter:
                Expect(p, 9, MessageType.Enter);
                return new EnterMessage((ScreenSide)ReadScreenSide(p[0]),
                                        BinaryPrimitives.ReadDoubleLittleEndian(p.Slice(1, 8)));
            case MessageType.Leave:
                Expect(p, 8, MessageType.Leave);
                return new LeaveMessage(BinaryPrimitives.ReadDoubleLittleEndian(p.Slice(0, 8)));
            case MessageType.ReleaseAll:
                Expect(p, 0, MessageType.ReleaseAll);
                return new ReleaseAllMessage();
            case MessageType.Clipboard:
                return new ClipboardMessage(DecodeText(p));
            case MessageType.ScreenInfo:
                Expect(p, 8, MessageType.ScreenInfo);
                return new ScreenInfoMessage(BinaryPrimitives.ReadInt32LittleEndian(p.Slice(0, 4)),
                                             BinaryPrimitives.ReadInt32LittleEndian(p.Slice(4, 4)));
            case MessageType.Ping:
                Expect(p, 8, MessageType.Ping);
                return new PingMessage(BinaryPrimitives.ReadInt64LittleEndian(p.Slice(0, 8)));
            case MessageType.Pong:
                Expect(p, 8, MessageType.Pong);
                return new PongMessage(BinaryPrimitives.ReadInt64LittleEndian(p.Slice(0, 8)));
            case MessageType.FileOffer:
                return DecodeFileOffer(p);
            case MessageType.FileChunk:
                return DecodeFileChunk(p);
            case MessageType.FileDone:
                return DecodeFileDone(p);
            case MessageType.FileCancel:
                return DecodeFileCancel(p);
            case MessageType.FileResult:
                return DecodeFileResult(p);
            case MessageType.OpenUrl:
                return DecodeOpenUrl(p);
            case MessageType.DropBatch:
                return DecodeDropBatch(p);
            default:
                throw new FormatException($"Tipo de mensagem desconhecido: {type}.");
        }
    }

    private static void Expect(ReadOnlySpan<byte> payload, int length, MessageType type)
    {
        if (payload.Length != length)
            throw new FormatException($"Tamanho de payload inválido para {type}: esperado {length}, recebido {payload.Length}.");
    }

    private static bool ReadBool(byte value)
    {
        if (value > 1)
            throw new FormatException($"Valor booleano inválido: {value}.");
        return value == 1;
    }

    private static byte ReadMouseButton(byte value)
    {
        if (value < 1 || value > MaxMouseButtonValue)
            throw new FormatException($"Botão do mouse inválido: {value}.");
        return value;
    }

    private static byte ReadScreenSide(byte value)
    {
        if (value > MaxScreenSideValue)
            throw new FormatException($"Lado de tela inválido: {value}.");
        return value;
    }

    private static Message DecodeFileOffer(ReadOnlySpan<byte> p)
    {
        var id = ReadGuid(ref p);
        var name = ReadString(ref p, FileTransferLimits.MaxFileNameBytes);
        var size = ReadInt64(ref p);
        ExpectEnd(p, MessageType.FileOffer);
        return new FileOfferMessage(id, name, size);
    }

    private static Message DecodeFileChunk(ReadOnlySpan<byte> p)
    {
        var id = ReadGuid(ref p);
        var offset = ReadInt64(ref p);
        var data = ReadByteArray(ref p);
        if (data.Length > FileTransferLimits.ChunkSize)
            throw new FormatException($"Bloco de arquivo grande demais: {data.Length} bytes.");
        ExpectEnd(p, MessageType.FileChunk);
        return new FileChunkMessage(id, offset, data);
    }

    private static Message DecodeFileDone(ReadOnlySpan<byte> p)
    {
        var id = ReadGuid(ref p);
        var sha256 = ReadByteArray(ref p);
        if (sha256.Length != 32)
            throw new FormatException($"SHA-256 inválido: esperado 32 bytes, recebido {sha256.Length}.");
        ExpectEnd(p, MessageType.FileDone);
        return new FileDoneMessage(id, sha256);
    }

    private static Message DecodeFileCancel(ReadOnlySpan<byte> p)
    {
        var id = ReadGuid(ref p);
        var reason = ReadString(ref p);
        ExpectEnd(p, MessageType.FileCancel);
        return new FileCancelMessage(id, reason);
    }

    private static Message DecodeFileResult(ReadOnlySpan<byte> p)
    {
        var id = ReadGuid(ref p);
        var ok = ReadBoolField(ref p);
        var error = ReadString(ref p);
        ExpectEnd(p, MessageType.FileResult);
        return new FileResultMessage(id, ok, error);
    }

    private static Message DecodeOpenUrl(ReadOnlySpan<byte> p)
    {
        var url = ReadString(ref p, MaxUrlBytes);
        ExpectEnd(p, MessageType.OpenUrl);
        return new OpenUrlMessage(url);
    }

    private static Message DecodeDropBatch(ReadOnlySpan<byte> p)
    {
        var count = ReadInt32(ref p);
        if (count < 0 || count > MaxDropBatchCount)
            throw new FormatException($"Quantidade de arquivos inválida no DropBatch: {count}.");
        var ids = new Guid[count];
        for (var i = 0; i < count; i++)
            ids[i] = ReadGuid(ref p);
        ExpectEnd(p, MessageType.DropBatch);
        return new DropBatchMessage(ids);
    }

    private static void ExpectEnd(ReadOnlySpan<byte> rest, MessageType type)
    {
        if (rest.Length != 0)
            throw new FormatException($"Sobraram {rest.Length} bytes no payload de {type}.");
    }

    private static void NeedBytes(ReadOnlySpan<byte> p, int count)
    {
        if (p.Length < count)
            throw new FormatException("Mensagem truncada.");
    }

    private static Guid ReadGuid(ref ReadOnlySpan<byte> p)
    {
        NeedBytes(p, 16);
        var id = new Guid(p.Slice(0, 16));
        p = p.Slice(16);
        return id;
    }

    private static long ReadInt64(ref ReadOnlySpan<byte> p)
    {
        NeedBytes(p, 8);
        var value = BinaryPrimitives.ReadInt64LittleEndian(p.Slice(0, 8));
        p = p.Slice(8);
        return value;
    }

    private static int ReadInt32(ref ReadOnlySpan<byte> p)
    {
        NeedBytes(p, 4);
        var value = BinaryPrimitives.ReadInt32LittleEndian(p.Slice(0, 4));
        p = p.Slice(4);
        return value;
    }

    private static bool ReadBoolField(ref ReadOnlySpan<byte> p)
    {
        NeedBytes(p, 1);
        var value = ReadBool(p[0]);
        p = p.Slice(1);
        return value;
    }

    /// <summary>String = int32 tamanho em bytes + UTF-8. maxBytes limita o tamanho do texto em bytes.</summary>
    private static string ReadString(ref ReadOnlySpan<byte> p, int maxBytes = int.MaxValue)
    {
        var length = ReadInt32(ref p);
        if (length < 0)
            throw new FormatException($"Tamanho de texto negativo: {length}.");
        if (length > p.Length)
            throw new FormatException($"Texto maior que o restante da mensagem: {length} bytes.");
        if (length > maxBytes)
            throw new FormatException($"Texto maior que o limite de {maxBytes} bytes: {length}.");
        var text = DecodeText(p.Slice(0, length));
        p = p.Slice(length);
        return text;
    }

    /// <summary>byte[] = int32 tamanho + bytes.</summary>
    private static byte[] ReadByteArray(ref ReadOnlySpan<byte> p)
    {
        var length = ReadInt32(ref p);
        if (length < 0)
            throw new FormatException($"Tamanho de bloco negativo: {length}.");
        if (length > p.Length)
            throw new FormatException($"Bloco maior que o restante da mensagem: {length} bytes.");
        var data = p.Slice(0, length).ToArray();
        p = p.Slice(length);
        return data;
    }

    private static string DecodeText(ReadOnlySpan<byte> payload)
    {
        try
        {
            return new UTF8Encoding(false, true).GetString(payload);
        }
        catch (DecoderFallbackException ex)
        {
            throw new FormatException("Texto UTF-8 inválido.", ex);
        }
    }
}
