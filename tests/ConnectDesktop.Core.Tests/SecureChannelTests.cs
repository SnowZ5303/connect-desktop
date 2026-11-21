using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using ConnectDesktop.Core.Net;
using ConnectDesktop.Core.Protocol;
using ConnectDesktop.Core.Security;
using ConnectDesktop.Core.Tests.Fakes;
using Xunit;

namespace ConnectDesktop.Core.Tests;

/// <summary>Testes de rede real em 127.0.0.1 (porta efêmera). Cada teste tem timeout de 10 s.</summary>
public sealed class SecureChannelTests : IDisposable
{
    private readonly TempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    private static DeviceIdentity NewIdentity(string name) => new(Guid.NewGuid().ToString("N"), name);

    private static CancellationTokenSource NewTimeout() => new(TimeSpan.FromSeconds(10));

    /// <summary>Canal que recebe os canais aceitos pelo listener, na ordem de chegada.</summary>
    private static Channel<IMessageChannel> Accepted(ControlListener listener)
    {
        var accepted = Channel.CreateUnbounded<IMessageChannel>();
        listener.ChannelAccepted += ch => accepted.Writer.TryWrite(ch);
        return accepted;
    }

    private static async Task<List<Message>> ReadN(IMessageChannel channel, int count, CancellationToken ct)
    {
        var list = new List<Message>();
        await foreach (var message in channel.ReadAllAsync(ct))
        {
            list.Add(message);
            if (list.Count == count) break;
        }
        return list;
    }

    [Fact]
    public async Task Pareamento_com_codigo_certo_conecta_e_salva_a_chave()
    {
        using var cts = NewTimeout();
        var secId = NewIdentity("Secundario");
        var priId = NewIdentity("Principal");
        var secStore = new PairingStore(_temp.File("sec.json"));
        var priStore = new PairingStore(_temp.File("pri.json"));

        await using var listener = new ControlListener(secId, secStore, () => "123456", 0);
        listener.Start();
        var accepted = Accepted(listener);

        var main = await SecureChannel.ConnectAsync("127.0.0.1", listener.Port, priId, priStore,
            () => Task.FromResult<string?>("123456"), cts.Token);
        await using var mainCh = main;
        var sec = await accepted.Reader.ReadAsync(cts.Token);
        await using var secCh = sec;

        Assert.Equal(secId.Id, main.RemoteDeviceId);
        Assert.Equal("Secundario", main.RemoteDeviceName);
        Assert.Equal(priId.Id, sec.RemoteDeviceId);
        Assert.Equal("Principal", sec.RemoteDeviceName);

        Assert.NotNull(priStore.GetKey(secId.Id));
        Assert.NotNull(secStore.GetKey(priId.Id));
    }

    [Fact]
    public async Task Codigo_errado_lanca_PairingException_e_dispara_HandshakeFailed()
    {
        using var cts = NewTimeout();
        var secId = NewIdentity("Secundario");
        var priId = NewIdentity("Principal");
        var priStore = new PairingStore(_temp.File("pri.json"));

        await using var listener = new ControlListener(secId, new PairingStore(_temp.File("sec.json")), () => "123456", 0);
        listener.Start();
        var failed = Channel.CreateUnbounded<string>();
        listener.HandshakeFailed += msg => failed.Writer.TryWrite(msg);

        await Assert.ThrowsAsync<PairingException>(() => SecureChannel.ConnectAsync("127.0.0.1", listener.Port, priId, priStore,
            () => Task.FromResult<string?>("000000"), cts.Token));

        var message = await failed.Reader.ReadAsync(cts.Token);
        Assert.False(string.IsNullOrWhiteSpace(message));
        Assert.Null(priStore.GetKey(secId.Id));
    }

    [Fact]
    public async Task Segunda_conexao_usa_a_chave_salva_sem_pedir_codigo()
    {
        using var cts = NewTimeout();
        var secId = NewIdentity("Secundario");
        var priId = NewIdentity("Principal");
        var priStore = new PairingStore(_temp.File("pri.json"));
        var secStore = new PairingStore(_temp.File("sec.json"));

        await using var listener = new ControlListener(secId, secStore, () => "123456", 0);
        listener.Start();
        var accepted = Accepted(listener);

        // Primeira conexão: pareamento com o código certo.
        var first = await SecureChannel.ConnectAsync("127.0.0.1", listener.Port, priId, priStore,
            () => Task.FromResult<string?>("123456"), cts.Token);
        var firstSec = await accepted.Reader.ReadAsync(cts.Token);
        await first.DisposeAsync();
        await firstSec.DisposeAsync();

        // Segunda conexão: não deve chamar askPairingCode.
        var askCalls = 0;
        await using var second = await SecureChannel.ConnectAsync("127.0.0.1", listener.Port, priId, priStore,
            () =>
            {
                Interlocked.Increment(ref askCalls);
                return Task.FromResult<string?>(null);
            }, cts.Token);
        await using var secondSec = await accepted.Reader.ReadAsync(cts.Token);

        Assert.Equal(0, Volatile.Read(ref askCalls));
        Assert.Equal(priId.Id, secondSec.RemoteDeviceId);

        // A conexão nova funciona de verdade.
        await second.SendAsync(new PingMessage(7), cts.Token);
        var received = await ReadN(secondSec, 1, cts.Token);
        Assert.Equal(new PingMessage(7), received[0]);
    }

    [Fact]
    public async Task Mensagens_nos_dois_sentidos_chegam_na_ordem()
    {
        using var cts = NewTimeout();
        var secId = NewIdentity("Secundario");
        var priId = NewIdentity("Principal");
        await using var listener = new ControlListener(secId, new PairingStore(_temp.File("sec.json")), () => "123456", 0);
        listener.Start();
        var accepted = Accepted(listener);

        var main = await SecureChannel.ConnectAsync("127.0.0.1", listener.Port, priId, new PairingStore(_temp.File("pri.json")),
            () => Task.FromResult<string?>("123456"), cts.Token);
        await using var mainCh = main;
        var sec = await accepted.Reader.ReadAsync(cts.Token);
        await using var secCh = sec;

        const int count = 100;
        var toSec = Enumerable.Range(0, count).Select(i => (Message)new MouseMoveMessage(i, -i)).ToList();
        var toMain = Enumerable.Range(0, count).Select(i => (Message)new ClipboardMessage($"texto {i} ação")).ToList();

        var readSec = ReadN(sec, count, cts.Token);
        var readMain = ReadN(main, count, cts.Token);

        foreach (var message in toSec)
            await main.SendAsync(message, cts.Token);
        foreach (var message in toMain)
            await sec.SendAsync(message, cts.Token);

        Assert.Equal(toSec, await readSec);
        Assert.Equal(toMain, await readMain);
    }

    [Fact]
    public async Task Muitas_mensagens_enviadas_em_paralelo_chegam_todas()
    {
        using var cts = NewTimeout();
        var secId = NewIdentity("Secundario");
        var priId = NewIdentity("Principal");
        await using var listener = new ControlListener(secId, new PairingStore(_temp.File("sec.json")), () => "123456", 0);
        listener.Start();
        var accepted = Accepted(listener);

        var main = await SecureChannel.ConnectAsync("127.0.0.1", listener.Port, priId, new PairingStore(_temp.File("pri.json")),
            () => Task.FromResult<string?>("123456"), cts.Token);
        await using var mainCh = main;
        var sec = await accepted.Reader.ReadAsync(cts.Token);
        await using var secCh = sec;

        const int count = 300;
        var readSec = ReadN(sec, count, cts.Token);

        var senders = Enumerable.Range(0, count)
            .Select(i => Task.Run(() => main.SendAsync(new MouseMoveMessage(i, 0), cts.Token)))
            .ToArray();
        await Task.WhenAll(senders);

        var received = await readSec;
        Assert.Equal(count, received.Count);
        Assert.All(received, m => Assert.IsType<MouseMoveMessage>(m));
        var dxs = received.Cast<MouseMoveMessage>().Select(m => m.DX).OrderBy(x => x).ToArray();
        Assert.Equal(Enumerable.Range(0, count).ToArray(), dxs);
    }

    [Fact]
    public async Task DisposeAsync_de_um_lado_faz_o_outro_terminar_a_leitura_e_disparar_Closed()
    {
        using var cts = NewTimeout();
        var secId = NewIdentity("Secundario");
        var priId = NewIdentity("Principal");
        await using var listener = new ControlListener(secId, new PairingStore(_temp.File("sec.json")), () => "123456", 0);
        listener.Start();
        var accepted = Accepted(listener);

        var main = await SecureChannel.ConnectAsync("127.0.0.1", listener.Port, priId, new PairingStore(_temp.File("pri.json")),
            () => Task.FromResult<string?>("123456"), cts.Token);
        var sec = await accepted.Reader.ReadAsync(cts.Token);

        var secClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mainClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sec.Closed += () => secClosed.TrySetResult();
        main.Closed += () => mainClosed.TrySetResult();

        // Leitura do lado secundário: deve terminar quando o principal fechar.
        var secReader = Task.Run(async () =>
        {
            try
            {
                await foreach (var _ in sec.ReadAllAsync(cts.Token))
                {
                }
            }
            catch (IOException)
            {
                // Fechamento abrupto da conexão também é aceitável: o importante é a leitura terminar.
            }
        }, cts.Token);

        await main.DisposeAsync();

        await secReader.WaitAsync(cts.Token);
        await secClosed.Task.WaitAsync(cts.Token);
        await mainClosed.Task.WaitAsync(cts.Token);

        await sec.DisposeAsync();
    }

    // ---- Casos negativos (cliente TCP cru, sem SecureChannel, para mandar quadros inválidos) ----

    private static async Task WriteRawFrame(NetworkStream stream, byte[] body, CancellationToken ct)
    {
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(0, 4), (uint)body.Length);
        body.CopyTo(frame, 4);
        await stream.WriteAsync(frame, ct);
    }

    /// <summary>Lê um quadro como o SecureChannel faz: fim de conexão ou erro de leitura viram IOException.</summary>
    private static async Task<byte[]> ReadRawFrameRequired(NetworkStream stream, CancellationToken ct)
    {
        try
        {
            var header = new byte[4];
            await ReadExactRaw(stream, header, ct);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
            var body = new byte[length];
            await ReadExactRaw(stream, body, ct);
            return body;
        }
        catch (SocketException ex)
        {
            throw new IOException("Conexão encerrada pelo outro PC.", ex);
        }
    }

    private static async Task ReadExactRaw(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), ct);
            if (read == 0)
                throw new IOException("Conexão encerrada pelo outro PC.");
            offset += read;
        }
    }

    private static byte[] RawClientHello(string tag, string id, string name)
    {
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            tag,
            id,
            name,
            pub = Convert.ToBase64String(ecdh.ExportSubjectPublicKeyInfo()),
            nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)),
        });
    }

    [Fact]
    public async Task ClientHello_com_tag_errada_dispara_HandshakeFailed_e_fecha_a_conexao()
    {
        using var cts = NewTimeout();
        await using var listener = new ControlListener(NewIdentity("Secundario"), new PairingStore(_temp.File("sec.json")), () => "123456", 0);
        listener.Start();
        var failed = Channel.CreateUnbounded<string>();
        listener.HandshakeFailed += msg => failed.Writer.TryWrite(msg);
        var acceptedCount = 0;
        listener.ChannelAccepted += _ => Interlocked.Increment(ref acceptedCount);

        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", listener.Port, cts.Token);
        var stream = client.GetStream();
        await WriteRawFrame(stream, RawClientHello("Outro-App/9", NewIdentity("x").Id, "Intruso"), cts.Token);

        // O listener não responde com ServerHello: o cliente recebe o fim da conexão como exceção.
        await Assert.ThrowsAsync<IOException>(() => ReadRawFrameRequired(stream, cts.Token));

        var message = await failed.Reader.ReadAsync(cts.Token);
        Assert.False(string.IsNullOrWhiteSpace(message));
        Assert.Equal(0, Volatile.Read(ref acceptedCount));
    }

    [Fact]
    public async Task Quadro_acima_do_limite_e_recusado()
    {
        using var cts = NewTimeout();
        var secId = NewIdentity("Secundario");
        await using var listener = new ControlListener(secId, new PairingStore(_temp.File("sec.json")), () => "123456", 0);
        listener.Start();
        var failed = Channel.CreateUnbounded<string>();
        listener.HandshakeFailed += msg => failed.Writer.TryWrite(msg);
        var acceptedCount = 0;
        listener.ChannelAccepted += _ => Interlocked.Increment(ref acceptedCount);

        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", listener.Port, cts.Token);
        var stream = client.GetStream();

        // ClientHello válido: o listener responde com o ServerHello.
        await WriteRawFrame(stream, RawClientHello(NetDefaults.AppTag, NewIdentity("x").Id, "Principal"), cts.Token);
        var serverHello = await ReadRawFrameRequired(stream, cts.Token);
        Assert.NotEmpty(serverHello);

        // ClientAuth com cabeçalho anunciando um quadro maior que MaxMessageSize + 64: só o cabeçalho é enviado.
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)(MessageCodec.MaxMessageSize + 65));
        await stream.WriteAsync(header, cts.Token);

        var message = await failed.Reader.ReadAsync(cts.Token);
        Assert.Contains("grande demais", message);
        Assert.Equal(0, Volatile.Read(ref acceptedCount));

        // A conexão foi fechada pelo listener.
        await Assert.ThrowsAsync<IOException>(() => ReadRawFrameRequired(stream, cts.Token));
    }

    [Fact]
    public async Task Modo_chave_com_ok_false_lanca_PairingException_e_mantem_a_chave_salva()
    {
        using var cts = NewTimeout();
        var secId = NewIdentity("Secundario");
        var priId = NewIdentity("Principal");

        // As duas pontas guardam chaves diferentes para o mesmo par: o handshake em modo chave falha.
        var secStore = new PairingStore(_temp.File("sec.json"));
        secStore.SetKey(priId.Id, "Principal", Enumerable.Repeat((byte)9, 32).ToArray());
        var priStore = new PairingStore(_temp.File("pri.json"));
        var keyOnMain = Enumerable.Repeat((byte)1, 32).ToArray();
        priStore.SetKey(secId.Id, "Secundario", keyOnMain);

        await using var listener = new ControlListener(secId, secStore, () => "123456", 0);
        listener.Start();

        var askCalls = 0;
        await Assert.ThrowsAsync<PairingException>(() => SecureChannel.ConnectAsync("127.0.0.1", listener.Port, priId, priStore,
            () =>
            {
                Interlocked.Increment(ref askCalls);
                return Task.FromResult<string?>("123456");
            }, cts.Token));

        Assert.Equal(0, Volatile.Read(ref askCalls));
        Assert.Equal(keyOnMain, priStore.GetKey(secId.Id));
    }

    [Fact]
    public async Task RemoteVersion_chega_aos_dois_lados()
    {
        using var cts = NewTimeout();
        var secId = new DeviceIdentity(Guid.NewGuid().ToString("N"), "Secundario", "1.0.3");
        var priId = new DeviceIdentity(Guid.NewGuid().ToString("N"), "Principal", "1.0.4");
        await using var listener = new ControlListener(secId, new PairingStore(_temp.File("sec.json")), () => "123456", 0);
        listener.Start();
        var accepted = Accepted(listener);

        var main = await SecureChannel.ConnectAsync("127.0.0.1", listener.Port, priId, new PairingStore(_temp.File("pri.json")),
            () => Task.FromResult<string?>("123456"), cts.Token);
        await using var mainCh = main;
        var sec = await accepted.Reader.ReadAsync(cts.Token);
        await using var secCh = sec;

        // Cada lado vê a versão do outro.
        Assert.Equal("1.0.3", main.RemoteVersion);
        Assert.Equal("1.0.4", sec.RemoteVersion);
    }
}
