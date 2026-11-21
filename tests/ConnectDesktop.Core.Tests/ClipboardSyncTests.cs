using ConnectDesktop.Core.Net;
using ConnectDesktop.Core.Protocol;
using ConnectDesktop.Core.Session;
using ConnectDesktop.Core.Tests.Fakes;
using Xunit;

namespace ConnectDesktop.Core.Tests;

/// <summary>
/// ClipboardSync verifica o clipboard a cada 500 ms. Os testes esperam pelo menos uma leitura
/// (Task.Delay de 700 ms depois do Attach) antes de mudar o clipboard, para que a linha de base seja tirada.
/// </summary>
public sealed class ClipboardSyncTests
{
    private const int PollWaitMs = 700;
    private const int MaxBytes = 512 * 1024;

    /// <summary>Lê o próximo ClipboardMessage que chegar no canal (até 3 s) e devolve o texto.</summary>
    private static async Task<string> ReadNextText(IMessageChannel channel)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await foreach (var message in channel.ReadAllAsync(cts.Token))
        {
            if (message is ClipboardMessage clipboard)
                return clipboard.Text;
        }
        throw new InvalidOperationException("O canal terminou sem chegar um ClipboardMessage.");
    }

    [Fact]
    public async Task Attach_nao_envia_o_texto_que_ja_estava_no_clipboard()
    {
        var clipboard = new FakeClipboard("texto antigo");
        using var sync = new ClipboardSync(clipboard);
        await using var pair = new FakeChannelPair();

        sync.Attach(pair.A);
        await Task.Delay(PollWaitMs);

        // Se o "texto antigo" tivesse sido enviado, ele chegaria antes do "texto novo".
        clipboard.Text = "texto novo";
        Assert.Equal("texto novo", await ReadNextText(pair.B));
    }

    [Fact]
    public async Task Mudanca_local_no_clipboard_e_enviada()
    {
        var clipboard = new FakeClipboard("inicial");
        using var sync = new ClipboardSync(clipboard);
        await using var pair = new FakeChannelPair();

        sync.Attach(pair.A);
        await Task.Delay(PollWaitMs);

        clipboard.Text = "copiado aqui: ação ✓ 😀";
        Assert.Equal("copiado aqui: ação ✓ 😀", await ReadNextText(pair.B));
    }

    [Fact]
    public async Task OnRemoteText_grava_no_clipboard_e_nao_ecoa_de_volta()
    {
        var clipboard = new FakeClipboard("local");
        using var sync = new ClipboardSync(clipboard);
        await using var pair = new FakeChannelPair();

        sync.Attach(pair.A);
        await Task.Delay(PollWaitMs);

        sync.OnRemoteText("do outro PC");

        Assert.Equal("do outro PC", clipboard.Text);
        Assert.Contains("do outro PC", clipboard.SetCalls);

        // Espera pelo menos duas leituras: o texto recebido não pode voltar para o outro PC.
        await Task.Delay(PollWaitMs * 2);

        // Próxima mudança local tem que ser a primeira mensagem que chega (sem eco antes dela).
        clipboard.Text = "marcador local";
        Assert.Equal("marcador local", await ReadNextText(pair.B));
    }

    [Fact]
    public async Task Texto_acima_de_512_KB_nao_e_enviado_nem_gravado()
    {
        var clipboard = new FakeClipboard("base");
        using var sync = new ClipboardSync(clipboard);
        await using var pair = new FakeChannelPair();

        sync.Attach(pair.A);
        await Task.Delay(PollWaitMs);

        var big = new string('a', MaxBytes + 1);

        // Vindo do outro PC: ignorado, não grava.
        sync.OnRemoteText(big);
        Assert.Empty(clipboard.SetCalls);

        // Copiado aqui: não é enviado.
        clipboard.Text = big;
        await Task.Delay(PollWaitMs * 2);

        clipboard.Text = "pequeno";
        Assert.Equal("pequeno", await ReadNextText(pair.B));
    }

    [Fact]
    public async Task Com_Enabled_false_nada_e_enviado_e_OnRemoteText_nao_grava()
    {
        var clipboard = new FakeClipboard("base");
        using var sync = new ClipboardSync(clipboard) { Enabled = false };
        await using var pair = new FakeChannelPair();

        sync.Attach(pair.A);
        await Task.Delay(PollWaitMs);

        sync.OnRemoteText("remoto");
        Assert.DoesNotContain("remoto", clipboard.SetCalls);
        Assert.NotEqual("remoto", clipboard.Text);

        clipboard.Text = "copiado com desligado";
        await Task.Delay(PollWaitMs * 2);

        // Liga de novo: só o que for copiado depois disso deve sair.
        sync.Enabled = true;
        clipboard.Text = "copiado com ligado";
        Assert.Equal("copiado com ligado", await ReadNextText(pair.B));
    }
}
