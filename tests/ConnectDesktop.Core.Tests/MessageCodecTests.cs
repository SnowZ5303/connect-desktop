using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using ConnectDesktop.Core.Layout;
using ConnectDesktop.Core.Platform;
using ConnectDesktop.Core.Protocol;
using Xunit;

namespace ConnectDesktop.Core.Tests;

public sealed class MessageCodecTests
{
    private static readonly Guid TransferA = Guid.Parse("11111111-2222-3333-4444-555555555555");

    public static IEnumerable<object[]> AllMessages() => new List<object[]>
    {
        new object[] { new MouseMoveMessage(-5, 300) },
        new object[] { new MouseMoveMessage(int.MinValue, int.MaxValue) },
        new object[] { new MouseButtonMessage(MouseButton.Left, true) },
        new object[] { new MouseButtonMessage(MouseButton.X2, false) },
        new object[] { new MouseWheelMessage(0, -120) },
        new object[] { new KeyMessage(0xE7, true) },
        new object[] { new KeyMessage(0x28, false) },
        new object[] { new EnterMessage(ScreenSide.Left, 0.25) },
        new object[] { new EnterMessage(ScreenSide.Bottom, 1.0) },
        new object[] { new LeaveMessage(0.0) },
        new object[] { new LeaveMessage(0.123456789) },
        new object[] { new ReleaseAllMessage() },
        new object[] { new ClipboardMessage("olá, mundo") },
        new object[] { new ClipboardMessage("Ação: çãõ é ñ ü 😀 👍🏽 世界") },
        new object[] { new ScreenInfoMessage(1920, 1080) },
        new object[] { new PingMessage(long.MaxValue) },
        new object[] { new PongMessage(1234567890123L) },
        new object[] { new FileOfferMessage(TransferA, "relatório ação.pdf", 123456789012L) },
        new object[] { new FileOfferMessage(TransferA, "vazio.txt", 0L) },
        new object[] { new FileChunkMessage(TransferA, 65536L, new byte[] { 1, 2, 3, 250, 255 }) },
        new object[] { new FileChunkMessage(TransferA, 0L, Array.Empty<byte>()) },
        new object[] { new FileDoneMessage(TransferA, SHA256.HashData(Encoding.UTF8.GetBytes("abc"))) },
        new object[] { new FileCancelMessage(TransferA, "Cancelado pelo usuário.") },
        new object[] { new FileResultMessage(TransferA, true, "") },
        new object[] { new FileResultMessage(TransferA, false, "Disco cheio") },
        new object[] { new OpenUrlMessage("https://exemplo.com/caminho?q=ação&x=1") },
        new object[] { new OpenUrlMessage("mailto:pessoa@exemplo.com") },
        new object[] { new DropBatchMessage(new[] { TransferA, Guid.Parse("66666666-7777-8888-9999-000000000000") }) },
        new object[] { new DropBatchMessage(Array.Empty<Guid>()) },
    };

    /// <summary>Compara por conteúdo: os records comparam byte[] por referência.</summary>
    private static void AssertSameMessage(Message expected, Message actual)
    {
        switch (expected)
        {
            case FileChunkMessage e:
                var chunk = Assert.IsType<FileChunkMessage>(actual);
                Assert.Equal(e.TransferId, chunk.TransferId);
                Assert.Equal(e.Offset, chunk.Offset);
                Assert.Equal(e.Data, chunk.Data);
                break;
            case FileDoneMessage e:
                var done = Assert.IsType<FileDoneMessage>(actual);
                Assert.Equal(e.TransferId, done.TransferId);
                Assert.Equal(e.Sha256, done.Sha256);
                break;
            case DropBatchMessage e:
                var batch = Assert.IsType<DropBatchMessage>(actual);
                Assert.Equal(e.TransferIds, batch.TransferIds);
                break;
            default:
                Assert.Equal(expected, actual);
                break;
        }
    }

    private static byte[] Bytes(params byte[][] parts)
    {
        var total = parts.Sum(p => p.Length);
        var result = new byte[total];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }
        return result;
    }

    private static byte[] Type(MessageType type) => new[] { (byte)type };

    private static byte[] I32(int value)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, value);
        return b;
    }

    private static byte[] I64(long value)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(b, value);
        return b;
    }

    private static byte[] Ascii(int count, byte value = (byte)'a') => Enumerable.Repeat(value, count).ToArray();

    [Fact]
    public void AllMessages_cobrem_todos_os_tipos_do_enum()
    {
        var covered = AllMessages().Select(o => ((Message)o[0]).Type).Distinct().OrderBy(t => t).ToList();
        var all = Enum.GetValues<MessageType>().OrderBy(t => t).ToList();
        Assert.Equal(all, covered);
    }

    [Theory]
    [MemberData(nameof(AllMessages))]
    public void RoundTrip_preserva_a_mensagem(Message message)
    {
        var bytes = MessageCodec.Encode(message);
        var decoded = MessageCodec.Decode(bytes);
        AssertSameMessage(message, decoded);
    }

    [Theory]
    [MemberData(nameof(AllMessages))]
    public void Encode_comeca_com_o_tipo_da_mensagem(Message message)
    {
        var bytes = MessageCodec.Encode(message);
        Assert.Equal((byte)message.Type, bytes[0]);
    }

    [Fact]
    public void Clipboard_com_acentos_e_emoji_volta_identico()
    {
        const string text = "Ação, coração, à è ì ò ù, ç, ã, õ — 😀 👨‍👩‍👧 日本語 Ελληνικά";
        var decoded = Assert.IsType<ClipboardMessage>(MessageCodec.Decode(MessageCodec.Encode(new ClipboardMessage(text))));
        Assert.Equal(text, decoded.Text);
    }

    [Fact]
    public void Clipboard_vazio_volta_vazio()
    {
        var decoded = Assert.IsType<ClipboardMessage>(MessageCodec.Decode(MessageCodec.Encode(new ClipboardMessage(""))));
        Assert.Equal("", decoded.Text);
    }

    [Fact]
    public void Decode_de_bytes_vazios_lanca_FormatException()
    {
        Assert.Throws<FormatException>(() => MessageCodec.Decode(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Decode_de_tipo_desconhecido_lanca_FormatException()
    {
        var known = Enum.GetValues<MessageType>().Select(t => (byte)t).ToHashSet();
        byte unknown = Enumerable.Range(0, 256).Select(i => (byte)i).First(b => !known.Contains(b));
        Assert.Throws<FormatException>(() => MessageCodec.Decode(new byte[] { unknown }));
        Assert.Throws<FormatException>(() => MessageCodec.Decode(new byte[] { unknown, 1, 2, 3, 4 }));
    }

    [Fact]
    public void Decode_de_mouse_move_com_tamanho_errado_lanca_FormatException()
    {
        // MouseMove precisa de 8 bytes de payload (dois int32).
        Assert.Throws<FormatException>(() => MessageCodec.Decode(new byte[] { (byte)MessageType.MouseMove, 1, 0, 0, 0 }));
        Assert.Throws<FormatException>(() => MessageCodec.Decode(new byte[] { (byte)MessageType.MouseMove }));
    }

    [Fact]
    public void Decode_de_ping_com_tamanho_errado_lanca_FormatException()
    {
        Assert.Throws<FormatException>(() => MessageCodec.Decode(new byte[] { (byte)MessageType.Ping, 1, 2, 3 }));
    }

    [Fact]
    public void Decode_de_enter_com_lado_invalido_lanca_FormatException()
    {
        // Enter: byte PrimarySide + double Position. 99 não é um ScreenSide válido.
        var bytes = MessageCodec.Encode(new EnterMessage(ScreenSide.Left, 0.5));
        bytes[1] = 99;
        Assert.Throws<FormatException>(() => MessageCodec.Decode(bytes));
    }

    [Theory]
    [MemberData(nameof(AllMessages))]
    public void Decode_de_mensagem_truncada_lanca_FormatException(Message message)
    {
        if (message.Type == MessageType.Clipboard)
            return; // texto: o payload é "o resto do buffer", então cortar um byte ainda é UTF-8 válido em alguns casos

        var bytes = MessageCodec.Encode(message);
        var truncated = bytes[..^1];
        Assert.Throws<FormatException>(() => MessageCodec.Decode(truncated));
    }

    [Fact]
    public void Decode_de_lixo_aleatorio_so_lanca_FormatException()
    {
        var rnd = new Random(42);
        for (var i = 0; i < 500; i++)
        {
            var data = new byte[rnd.Next(0, 64)];
            rnd.NextBytes(data);
            try
            {
                var message = MessageCodec.Decode(data);
                Assert.NotNull(message);
            }
            catch (FormatException)
            {
                // esperado para lixo
            }
        }
    }

    // ---- Transferência de arquivos: casos inválidos (bytes montados à mão, porque o Encode recusa alguns) ----

    [Fact]
    public void FileOffer_com_tamanho_de_nome_negativo_lanca_FormatException()
    {
        var raw = Bytes(Type(MessageType.FileOffer), TransferA.ToByteArray(), I32(-1), I64(0));
        Assert.Throws<FormatException>(() => MessageCodec.Decode(raw));
    }

    [Fact]
    public void FileOffer_com_nome_maior_que_o_restante_lanca_FormatException()
    {
        // Diz 500 bytes de nome, mas só sobram 3 bytes de nome + 8 de tamanho.
        var raw = Bytes(Type(MessageType.FileOffer), TransferA.ToByteArray(), I32(500), Ascii(3), I64(0));
        Assert.Throws<FormatException>(() => MessageCodec.Decode(raw));
    }

    [Fact]
    public void FileOffer_com_nome_acima_de_MaxFileNameBytes_lanca_FormatException()
    {
        var max = FileTransferLimits.MaxFileNameBytes;
        var raw = Bytes(Type(MessageType.FileOffer), TransferA.ToByteArray(), I32(max + 1), Ascii(max + 1), I64(10));
        Assert.Throws<FormatException>(() => MessageCodec.Decode(raw));
    }

    [Fact]
    public void FileOffer_com_bytes_sobrando_no_fim_lanca_FormatException()
    {
        var raw = Bytes(MessageCodec.Encode(new FileOfferMessage(TransferA, "a.txt", 3)), new byte[] { 0xFF });
        Assert.Throws<FormatException>(() => MessageCodec.Decode(raw));
    }

    [Fact]
    public void FileChunk_com_Data_acima_de_ChunkSize_lanca_FormatException()
    {
        var size = FileTransferLimits.ChunkSize + 1;
        var raw = Bytes(Type(MessageType.FileChunk), TransferA.ToByteArray(), I64(0), I32(size), Ascii(size));
        Assert.Throws<FormatException>(() => MessageCodec.Decode(raw));
    }

    [Fact]
    public void FileChunk_com_bytes_sobrando_no_fim_lanca_FormatException()
    {
        var raw = Bytes(MessageCodec.Encode(new FileChunkMessage(TransferA, 0, new byte[] { 1, 2 })), new byte[] { 0x00 });
        Assert.Throws<FormatException>(() => MessageCodec.Decode(raw));
    }

    [Fact]
    public void FileDone_com_Sha256_diferente_de_32_bytes_lanca_FormatException()
    {
        var raw = Bytes(Type(MessageType.FileDone), TransferA.ToByteArray(), I32(31), Ascii(31));
        Assert.Throws<FormatException>(() => MessageCodec.Decode(raw));
    }

    [Fact]
    public void FileDone_com_bytes_sobrando_no_fim_lanca_FormatException()
    {
        var raw = Bytes(MessageCodec.Encode(new FileDoneMessage(TransferA, new byte[32])), new byte[] { 0x01 });
        Assert.Throws<FormatException>(() => MessageCodec.Decode(raw));
    }

    [Fact]
    public void FileCancel_com_motivo_de_tamanho_negativo_lanca_FormatException()
    {
        var raw = Bytes(Type(MessageType.FileCancel), TransferA.ToByteArray(), I32(-1));
        Assert.Throws<FormatException>(() => MessageCodec.Decode(raw));
    }

    [Fact]
    public void FileResult_com_bytes_sobrando_no_fim_lanca_FormatException()
    {
        var raw = Bytes(MessageCodec.Encode(new FileResultMessage(TransferA, true, "")), new byte[] { 0xFF });
        Assert.Throws<FormatException>(() => MessageCodec.Decode(raw));
    }

    // ---- v1.0.4: OpenUrl e DropBatch (casos inválidos com bytes montados à mão) ----

    [Fact]
    public void OpenUrl_com_tamanho_de_texto_negativo_lanca_FormatException()
    {
        var raw = Bytes(Type(MessageType.OpenUrl), I32(-1));
        Assert.Throws<FormatException>(() => MessageCodec.Decode(raw));
    }

    [Fact]
    public void OpenUrl_com_texto_maior_que_o_restante_lanca_FormatException()
    {
        var raw = Bytes(Type(MessageType.OpenUrl), I32(50), Ascii(3));
        Assert.Throws<FormatException>(() => MessageCodec.Decode(raw));
    }

    [Fact]
    public void OpenUrl_com_bytes_sobrando_no_fim_lanca_FormatException()
    {
        var raw = Bytes(MessageCodec.Encode(new OpenUrlMessage("https://exemplo.com")), new byte[] { 0xFF });
        Assert.Throws<FormatException>(() => MessageCodec.Decode(raw));
    }

    [Fact]
    public void DropBatch_com_quantidade_negativa_lanca_FormatException()
    {
        var raw = Bytes(Type(MessageType.DropBatch), I32(-1));
        Assert.Throws<FormatException>(() => MessageCodec.Decode(raw));
    }

    [Fact]
    public void DropBatch_com_quantidade_acima_de_1000_lanca_FormatException()
    {
        // 1001 Guids completos: o limite de quantidade é que tem que recusar.
        var ids = Enumerable.Range(0, 1001).SelectMany(_ => Guid.NewGuid().ToByteArray()).ToArray();
        var raw = Bytes(Type(MessageType.DropBatch), I32(1001), ids);
        Assert.Throws<FormatException>(() => MessageCodec.Decode(raw));
    }

    [Fact]
    public void DropBatch_com_quantidade_maior_que_os_guids_presentes_lanca_FormatException()
    {
        // Diz 2 Guids, mas só traz 1.
        var raw = Bytes(Type(MessageType.DropBatch), I32(2), TransferA.ToByteArray());
        Assert.Throws<FormatException>(() => MessageCodec.Decode(raw));
    }

    [Fact]
    public void DropBatch_com_bytes_sobrando_no_fim_lanca_FormatException()
    {
        var raw = Bytes(MessageCodec.Encode(new DropBatchMessage(new[] { TransferA })), new byte[] { 0x01 });
        Assert.Throws<FormatException>(() => MessageCodec.Decode(raw));
    }
}
