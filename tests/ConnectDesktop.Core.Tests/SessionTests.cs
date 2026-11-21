using System.Collections.Concurrent;
using ConnectDesktop.Core.Layout;
using ConnectDesktop.Core.Platform;
using ConnectDesktop.Core.Protocol;
using ConnectDesktop.Core.Session;
using ConnectDesktop.Core.Tests.Fakes;
using ConnectDesktop.Core.Transfer;
using Xunit;

namespace ConnectDesktop.Core.Tests;

/// <summary>
/// Principal e secundário ligados por um par de canais em memória, com os fakes de captura, injeção e tela.
/// Principal na borda direita (secundário à direita do principal, entra pela esquerda do secundário).
/// </summary>
public sealed class SessionTests
{
    private const ushort HidA = 0x04;
    private const ushort HidEsc = HidKeys.Escape;
    private const ushort HidLeftCtrl = HidKeys.LeftCtrl;
    private const ushort HidLeftAlt = HidKeys.LeftAlt;

    private sealed class Rig : IAsyncDisposable
    {
        public readonly FakeChannelPair Pair = new();
        public readonly FakeCapture Capture = new();
        public readonly FakeScreen PrimaryScreen = new(new ScreenBounds(0, 0, 1920, 1080));
        public readonly FakeScreen SecondaryScreen = new(new ScreenBounds(0, 0, 1920, 1080));
        public readonly FakeInjector Injector = new();
        public readonly PrimarySession Primary;
        public readonly SecondarySession Secondary;

        public Rig()
        {
            Primary = new PrimarySession(Pair.A, Capture, PrimaryScreen, ScreenSide.Right, null);
            Secondary = new SecondarySession(Pair.B, Injector, SecondaryScreen, null);
        }

        public async Task StartAndWaitReadyAsync()
        {
            Secondary.Start();
            Primary.Start();
            await Wait.Until(() => Primary.RemoteScreenSize == (1920, 1080), what: "ScreenInfo do secundário");
        }

        /// <summary>Move o cursor do principal para a borda direita e espera o secundário ficar ativo.</summary>
        public async Task EnterRemoteAsync()
        {
            Capture.Move(1919, 500, 5, 0);
            await Wait.Until(() => Primary.State == PrimaryState.Remote, what: "principal em Remote");
            await Wait.Until(() => Secondary.IsActive, what: "secundário ativo");
        }

        public async ValueTask DisposeAsync()
        {
            await Primary.DisposeAsync();
            await Secondary.DisposeAsync();
            await Pair.DisposeAsync();
        }
    }

    private static MoveAbsoluteAction LastMove(FakeInjector injector) =>
        injector.Actions.OfType<MoveAbsoluteAction>().Last();

    [Fact]
    public async Task Borda_direita_entra_no_secundario_pela_borda_esquerda()
    {
        await using var rig = new Rig();
        await rig.StartAndWaitReadyAsync();

        rig.Capture.Move(1919, 500, 5, 0);

        await Wait.Until(() => rig.Primary.State == PrimaryState.Remote, what: "principal em Remote");
        await Wait.Until(() => rig.Secondary.IsActive, what: "secundário ativo");
        await Wait.Until(() => rig.Injector.Actions.OfType<MoveAbsoluteAction>().Any(), what: "MoveMouseAbsolute injetado");

        Assert.Equal(ScreenSide.Left, rig.Secondary.PrimarySide);
        Assert.True(rig.Capture.SuppressLocalInput);

        var move = LastMove(rig.Injector);
        Assert.InRange(move.X, 0, 10);
        Assert.InRange(move.Y, 498, 502);
    }

    [Fact]
    public async Task Movimentos_relativos_movem_o_cursor_do_secundario()
    {
        await using var rig = new Rig();
        await rig.StartAndWaitReadyAsync();
        await rig.EnterRemoteAsync();
        await Wait.Until(() => rig.Injector.Actions.OfType<MoveAbsoluteAction>().Any(), what: "posição inicial injetada");

        var before = LastMove(rig.Injector);

        rig.Capture.Move(960, 500, 10, 3);

        await Wait.Until(() =>
        {
            var last = rig.Injector.Actions.OfType<MoveAbsoluteAction>().LastOrDefault();
            return last is not null && last.X == before.X + 10 && last.Y == before.Y + 3;
        }, what: "cursor do secundário andou (+10, +3)");

        var after = LastMove(rig.Injector);
        Assert.Equal(before.X + 10, after.X);
        Assert.Equal(before.Y + 3, after.Y);
    }

    [Fact]
    public async Task Mover_para_a_esquerda_alem_da_borda_volta_para_local_e_posiciona_na_borda_direita()
    {
        await using var rig = new Rig();
        await rig.StartAndWaitReadyAsync();
        await rig.EnterRemoteAsync();
        await Wait.Until(() => rig.Injector.Actions.OfType<MoveAbsoluteAction>().Any(), what: "posição inicial injetada");

        // Puxa bem para a esquerda: o secundário sai pela borda Left e manda Leave.
        rig.Capture.Move(0, 500, -500, 0);

        await Wait.Until(() => rig.Primary.State == PrimaryState.Local, what: "principal voltou a Local");
        Assert.False(rig.Capture.SuppressLocalInput);
        Assert.False(rig.Secondary.IsActive);

        await Wait.Until(() => rig.PrimaryScreen.SetCursorCalls.Count > 0, what: "SetCursorPosition no principal");
        var (x, y) = rig.PrimaryScreen.SetCursorCalls[^1];
        Assert.InRange(x, 1900, 1919);
        Assert.InRange(y, 490, 510);
    }

    [Fact]
    public async Task Teclas_no_modo_Remote_chegam_ao_injetor_e_nao_no_Local()
    {
        await using var rig = new Rig();
        await rig.StartAndWaitReadyAsync();

        // Em Local: a tecla não deve ser enviada ao secundário.
        rig.Capture.Key(0x05, true);
        rig.Capture.Key(0x05, false);

        await rig.EnterRemoteAsync();

        rig.Capture.Key(HidA, true);
        rig.Capture.Key(HidA, false);

        await Wait.Until(() => rig.Injector.Actions.OfType<KeyAction>().Any(k => k.Hid == HidA && !k.Down),
            what: "tecla chegou ao injetor");

        var keys = rig.Injector.Actions.OfType<KeyAction>().ToList();
        Assert.Contains(new KeyAction(HidA, true), keys);
        Assert.Contains(new KeyAction(HidA, false), keys);
        Assert.DoesNotContain(keys, k => k.Hid == 0x05);
    }

    [Fact]
    public async Task Ctrl_Alt_Esc_volta_para_Local_e_secundario_solta_as_teclas()
    {
        await using var rig = new Rig();
        await rig.StartAndWaitReadyAsync();
        await rig.EnterRemoteAsync();

        rig.Capture.Key(HidLeftCtrl, true);
        rig.Capture.Key(HidLeftAlt, true);
        rig.Capture.Key(HidEsc, true);

        await Wait.Until(() => rig.Primary.State == PrimaryState.Local, what: "principal voltou a Local");
        Assert.False(rig.Capture.SuppressLocalInput);

        await Wait.Until(() =>
        {
            var keys = rig.Injector.Actions.OfType<KeyAction>().ToList();
            return keys.Contains(new KeyAction(HidLeftCtrl, false)) && keys.Contains(new KeyAction(HidLeftAlt, false));
        }, what: "secundário soltou Ctrl e Alt");
    }

    [Fact]
    public async Task Canal_fechado_em_Remote_volta_o_principal_para_Local()
    {
        await using var rig = new Rig();
        await rig.StartAndWaitReadyAsync();
        await rig.EnterRemoteAsync();

        // Fecha o lado do secundário: o principal recebe o Closed do canal.
        await rig.Pair.B.DisposeAsync();

        await Wait.Until(() => rig.Primary.State == PrimaryState.Local, what: "principal voltou a Local");
        Assert.False(rig.Capture.SuppressLocalInput);
    }

    private static bool HasAction(FakeInjector injector, InjectedAction action) => injector.Actions.Contains(action);

    [Fact]
    public async Task Up_sem_down_enviado_nao_e_mandado_pelo_principal()
    {
        await using var rig = new Rig();
        await rig.StartAndWaitReadyAsync();

        // Tecla pressionada no Local e solta depois de entrar em Remote: o secundário nunca recebeu o down.
        rig.Capture.Key(0x05, true);
        await rig.EnterRemoteAsync();
        rig.Capture.Key(0x05, false);
        // Tecla que nunca foi pressionada: só o up chega.
        rig.Capture.Key(0x07, false);

        // Marcador: um down/up enviado depois garante que tudo anterior já foi processado pelo secundário.
        rig.Capture.Key(HidA, true);
        rig.Capture.Key(HidA, false);
        await Wait.Until(() => HasAction(rig.Injector, new KeyAction(HidA, false)), what: "marcador chegou ao injetor");

        var keys = rig.Injector.Actions.OfType<KeyAction>().ToList();
        Assert.DoesNotContain(keys, k => k.Hid == 0x05);
        Assert.DoesNotContain(keys, k => k.Hid == 0x07);
    }

    [Fact]
    public async Task ReleaseAll_solta_teclas_e_botoes_pressionados_no_secundario()
    {
        await using var rig = new Rig();
        await rig.StartAndWaitReadyAsync();
        await rig.EnterRemoteAsync();

        rig.Capture.Button(MouseButton.Left, true);
        rig.Capture.Key(HidA, true);
        rig.Capture.Key(HidLeftCtrl, true);
        rig.Capture.Key(HidLeftAlt, true);
        await Wait.Until(() => HasAction(rig.Injector, new MouseButtonAction(MouseButton.Left, true))
                               && HasAction(rig.Injector, new KeyAction(HidA, true)), what: "botão e tecla pressionados");

        // Atalho Ctrl+Alt+Esc manda ReleaseAll.
        rig.Capture.Key(HidEsc, true);

        await Wait.Until(() => rig.Primary.State == PrimaryState.Local, what: "principal voltou a Local");
        await Wait.Until(() => HasAction(rig.Injector, new MouseButtonAction(MouseButton.Left, false))
                               && HasAction(rig.Injector, new KeyAction(HidA, false))
                               && HasAction(rig.Injector, new KeyAction(HidLeftCtrl, false))
                               && HasAction(rig.Injector, new KeyAction(HidLeftAlt, false)),
            what: "ReleaseAll soltou botão e teclas");
        Assert.False(rig.Secondary.IsActive);
    }

    [Fact]
    public async Task Leave_solta_teclas_e_botoes_pressionados_no_secundario()
    {
        await using var rig = new Rig();
        await rig.StartAndWaitReadyAsync();
        await rig.EnterRemoteAsync();

        rig.Capture.Button(MouseButton.Right, true);
        rig.Capture.Key(HidA, true);
        await Wait.Until(() => HasAction(rig.Injector, new MouseButtonAction(MouseButton.Right, true))
                               && HasAction(rig.Injector, new KeyAction(HidA, true)), what: "botão e tecla pressionados");

        // Puxa para a esquerda além da borda: o secundário manda Leave.
        rig.Capture.Move(0, 500, -500, 0);

        await Wait.Until(() => rig.Primary.State == PrimaryState.Local, what: "principal voltou a Local");
        await Wait.Until(() => HasAction(rig.Injector, new MouseButtonAction(MouseButton.Right, false))
                               && HasAction(rig.Injector, new KeyAction(HidA, false)),
            what: "Leave soltou botão e tecla");
        Assert.False(rig.Secondary.IsActive);
    }

    [Fact]
    public async Task Canal_fechado_solta_teclas_e_botoes_pressionados_no_secundario()
    {
        await using var rig = new Rig();
        await rig.StartAndWaitReadyAsync();
        await rig.EnterRemoteAsync();

        rig.Capture.Button(MouseButton.Middle, true);
        rig.Capture.Key(HidA, true);
        await Wait.Until(() => HasAction(rig.Injector, new MouseButtonAction(MouseButton.Middle, true))
                               && HasAction(rig.Injector, new KeyAction(HidA, true)), what: "botão e tecla pressionados");

        // Fecha o canal do lado do principal: o secundário deve ler o fim e soltar tudo.
        await rig.Pair.A.DisposeAsync();

        await Wait.Until(() => HasAction(rig.Injector, new MouseButtonAction(MouseButton.Middle, false))
                               && HasAction(rig.Injector, new KeyAction(HidA, false)),
            what: "canal fechado soltou botão e tecla");
        await Wait.Until(() => !rig.Secondary.IsActive, what: "secundário inativo");
    }

    [Fact]
    public async Task Arquivo_enviado_pelo_principal_chega_ao_Files_do_secundario_e_IsConnected_cai_ao_fechar()
    {
        using var temp = new TempDir();
        var sourcePath = temp.File("origem.txt");
        File.WriteAllText(sourcePath, "conteúdo via sessão");
        var secondaryDir = temp.File("recebidos");

        await using var pair = new FakeChannelPair();
        using var filesPrimary = new FileTransferService(temp.File("enviados"));
        using var filesSecondary = new FileTransferService(secondaryDir);

        await using var primary = new PrimarySession(pair.A, new FakeCapture(), new FakeScreen(), ScreenSide.Right, null)
        {
            Files = filesPrimary,
        };
        await using var secondary = new SecondarySession(pair.B, new FakeInjector(), new FakeScreen(), null)
        {
            Files = filesSecondary,
        };

        secondary.Start();
        primary.Start();
        await Wait.Until(() => filesPrimary.IsConnected && filesSecondary.IsConnected, what: "Files ligados ao canal");

        var id = filesPrimary.SendFile(sourcePath);

        await Wait.Until(() => filesSecondary.Transfers.Any(t => t.Id == id && t.State == TransferState.Completed),
            what: "arquivo recebido pelo secundário");
        var received = filesSecondary.Transfers.First(t => t.Id == id);
        Assert.Equal("origem.txt", received.FileName);
        Assert.Equal(File.ReadAllBytes(sourcePath), File.ReadAllBytes(received.LocalPath!));

        // Fechar o canal: os dois serviços deixam de estar conectados.
        await pair.B.DisposeAsync();
        await Wait.Until(() => !filesSecondary.IsConnected, what: "IsConnected do secundário virou false");
        await Wait.Until(() => !filesPrimary.IsConnected, what: "IsConnected do principal virou false");
    }

    [Fact]
    public async Task AppMessage_dispara_para_OpenUrl_e_DropBatch_nos_dois_sentidos()
    {
        await using var pair = new FakeChannelPair();
        await using var primary = new PrimarySession(pair.A, new FakeCapture(), new FakeScreen(), ScreenSide.Right, null);
        await using var secondary = new SecondarySession(pair.B, new FakeInjector(), new FakeScreen(), null);

        var atPrimary = new ConcurrentQueue<Message>();
        var atSecondary = new ConcurrentQueue<Message>();
        primary.AppMessage += atPrimary.Enqueue;
        secondary.AppMessage += atSecondary.Enqueue;

        secondary.Start();
        primary.Start();

        // Secundário -> principal.
        var batch = new DropBatchMessage(new[] { Guid.NewGuid(), Guid.NewGuid() });
        await pair.B.SendAsync(new OpenUrlMessage("https://exemplo.com/ação"));
        await pair.B.SendAsync(batch);
        // O AppMessage é despachado em outra tarefa: a ordem não é garantida, então confere o conteúdo.
        await Wait.Until(() => atPrimary.Count == 2, what: "duas mensagens de app no principal");
        Assert.Contains(new OpenUrlMessage("https://exemplo.com/ação"), atPrimary);
        Assert.Equal(batch.TransferIds, atPrimary.OfType<DropBatchMessage>().Single().TransferIds);

        // Principal -> secundário.
        var back = new DropBatchMessage(new[] { Guid.NewGuid() });
        await pair.A.SendAsync(new OpenUrlMessage("mailto:pessoa@exemplo.com"));
        await pair.A.SendAsync(back);
        await Wait.Until(() => atSecondary.Count == 2, what: "duas mensagens de app no secundário");
        Assert.Contains(new OpenUrlMessage("mailto:pessoa@exemplo.com"), atSecondary);
        Assert.Equal(back.TransferIds, atSecondary.OfType<DropBatchMessage>().Single().TransferIds);
    }

    [Fact]
    public async Task Botao_pressionado_na_borda_nao_entra_em_Remote_ate_soltar()
    {
        await using var rig = new Rig();
        await rig.StartAndWaitReadyAsync();

        // Botão esquerdo preso: encostar na borda não pode trocar de tela.
        rig.Capture.Button(MouseButton.Left, true);
        rig.Capture.Move(1919, 500, 5, 0);
        Assert.Equal(PrimaryState.Local, rig.Primary.State);
        Assert.False(rig.Capture.SuppressLocalInput);

        // Soltou o botão: a mesma encostada agora entra em Remote.
        rig.Capture.Button(MouseButton.Left, false);
        rig.Capture.Move(1919, 500, 5, 0);
        await Wait.Until(() => rig.Primary.State == PrimaryState.Remote, what: "principal entrou em Remote");
        Assert.True(rig.Capture.SuppressLocalInput);
    }
}
