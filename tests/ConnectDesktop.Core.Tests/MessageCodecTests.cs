using ConnectDesktop.Core.Layout;
using ConnectDesktop.Core.Platform;
using ConnectDesktop.Core.Protocol;
using Xunit;

namespace ConnectDesktop.Core.Tests;

public sealed class MessageCodecTests
{
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
    };

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
        Assert.Equal(message, decoded);
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
}
