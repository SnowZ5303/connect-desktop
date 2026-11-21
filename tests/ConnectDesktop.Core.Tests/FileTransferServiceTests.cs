using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using ConnectDesktop.Core.Net;
using ConnectDesktop.Core.Protocol;
using ConnectDesktop.Core.Tests.Fakes;
using ConnectDesktop.Core.Transfer;
using Xunit;

namespace ConnectDesktop.Core.Tests;

/// <summary>
/// Dois FileTransferService ligados por um par de canais em memória: A envia, B recebe.
/// Cada teste tem pastas temporárias próprias. O canal de A passa por <see cref="DelayedChannel"/> para
/// que cancelamentos e quedas aconteçam no meio da transferência.
/// </summary>
public sealed class FileTransferServiceTests
{
    private const int LongWaitMs = 10000;

    private sealed class Harness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _pumpCts = new();

        public readonly TempDir Temp = new();
        public readonly string SrcDir;
        public readonly string DstA;
        public readonly string DstB;
        public readonly FakeChannelPair Pair = new();
        public readonly FileTransferService A;
        public readonly FileTransferService B;

        public Harness(int delayMs = 0)
        {
            SrcDir = Temp.File("src");
            DstA = Temp.File("dstA");
            DstB = Temp.File("dstB");
            Directory.CreateDirectory(SrcDir);

            A = new FileTransferService(DstA);
            B = new FileTransferService(DstB);
            A.Attach(new DelayedChannel(Pair.A, delayMs));
            B.Attach(Pair.B);

            // Mensagens que chegam em cada lado vão para o serviço daquele lado.
            Pump(Pair.A, A);
            Pump(Pair.B, B);
        }

        private void Pump(IMessageChannel channel, FileTransferService service)
        {
            var ct = _pumpCts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await foreach (var message in channel.ReadAllAsync(ct))
                        service.OnRemoteMessage(message);
                }
                catch (OperationCanceledException)
                {
                }
                catch (IOException)
                {
                }
            });
        }

        public string WriteSource(string name, byte[] data)
        {
            var path = Path.Combine(SrcDir, name);
            File.WriteAllBytes(path, data);
            return path;
        }

        public async ValueTask DisposeAsync()
        {
            A.Dispose();
            B.Dispose();
            _pumpCts.Cancel();
            await Pair.DisposeAsync();
            Temp.Dispose();
        }
    }

    private static bool IsFinal(TransferState state) =>
        state is TransferState.Completed or TransferState.Failed or TransferState.Canceled;

    private static async Task<TransferInfo> WaitFinal(FileTransferService service, Guid id)
    {
        await Wait.Until(() => service.Transfers.Any(t => t.Id == id && IsFinal(t.State)), LongWaitMs,
            "transferência terminar");
        return service.Transfers.First(t => t.Id == id);
    }

    private static async Task WaitInProgress(FileTransferService service, Guid id)
    {
        await Wait.Until(() => service.Transfers.Any(t => t.Id == id && t.State == TransferState.InProgress),
            LongWaitMs, "transferência em andamento");
    }

    private static bool NoPartFiles(string dir) =>
        !Directory.Exists(dir) || Directory.GetFiles(dir, "*.part", SearchOption.AllDirectories).Length == 0;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(64 * 1024)]
    [InlineData(3 * 1024 * 1024)]
    public async Task Envio_chega_com_conteudo_identico(int size)
    {
        await using var h = new Harness();
        var data = RandomNumberGenerator.GetBytes(size);
        var id = h.A.SendFile(h.WriteSource("arquivo.bin", data));

        var sent = await WaitFinal(h.A, id);
        Assert.Equal(TransferState.Completed, sent.State);

        var received = await WaitFinal(h.B, id);
        Assert.Equal(TransferState.Completed, received.State);
        Assert.Equal("arquivo.bin", received.FileName);
        Assert.Equal(size, received.Size);
        Assert.NotNull(received.LocalPath);
        Assert.Equal(data, File.ReadAllBytes(received.LocalPath!));
        Assert.True(NoPartFiles(h.DstB));
    }

    [Fact]
    public async Task Nome_colidindo_ganha_sufixo_e_o_arquivo_existente_fica_intacto()
    {
        await using var h = new Harness();
        Directory.CreateDirectory(h.DstB);
        File.WriteAllText(Path.Combine(h.DstB, "a.txt"), "antigo");

        var id = h.A.SendFile(h.WriteSource("a.txt", Encoding.UTF8.GetBytes("novo")));
        var received = await WaitFinal(h.B, id);

        Assert.Equal(TransferState.Completed, received.State);
        Assert.Equal("a (1).txt", received.FileName);
        Assert.Equal("antigo", File.ReadAllText(Path.Combine(h.DstB, "a.txt")));
        Assert.Equal("novo", File.ReadAllText(Path.Combine(h.DstB, "a (1).txt")));
    }

    [Theory]
    [InlineData("..\\..\\x.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("CON.txt")]
    [InlineData("a:b?.txt")]
    public async Task Nome_malicioso_fica_dentro_da_pasta_de_destino(string name)
    {
        await using var h = new Harness();
        var id = Guid.NewGuid();
        var empty = SHA256.HashData(Array.Empty<byte>());

        // Injeta a oferta crua no canal de A: o B tem que higienizar o nome.
        await h.Pair.A.SendAsync(new FileOfferMessage(id, name, 0));
        await h.Pair.A.SendAsync(new FileDoneMessage(id, empty));

        var received = await WaitFinal(h.B, id);
        Assert.Equal(TransferState.Completed, received.State);
        Assert.NotNull(received.LocalPath);

        var root = Path.GetFullPath(h.DstB).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(received.LocalPath!);
        Assert.StartsWith(root, full, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(full));
        Assert.Equal(Path.GetFullPath(h.DstB).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(full));
    }

    [Fact]
    public async Task Cancelamento_pelo_remetente_termina_os_dois_lados_sem_part()
    {
        await using var h = new Harness(delayMs: 15);
        var id = h.A.SendFile(h.WriteSource("grande.bin", RandomNumberGenerator.GetBytes(3 * 1024 * 1024)));
        await WaitInProgress(h.B, id);

        h.A.Cancel(id);

        Assert.Equal(TransferState.Canceled, (await WaitFinal(h.A, id)).State);
        Assert.Equal(TransferState.Canceled, (await WaitFinal(h.B, id)).State);
        Assert.True(NoPartFiles(h.DstB));
    }

    [Fact]
    public async Task Cancelamento_pelo_destinatario_termina_os_dois_lados_sem_part()
    {
        await using var h = new Harness(delayMs: 15);
        var id = h.A.SendFile(h.WriteSource("grande.bin", RandomNumberGenerator.GetBytes(3 * 1024 * 1024)));
        await WaitInProgress(h.B, id);

        h.B.Cancel(id);

        Assert.Equal(TransferState.Canceled, (await WaitFinal(h.B, id)).State);
        Assert.Equal(TransferState.Canceled, (await WaitFinal(h.A, id)).State);
        Assert.True(NoPartFiles(h.DstB));
    }

    [Fact]
    public async Task Queda_do_canal_no_meio_falha_com_Conexao_perdida_e_sem_part()
    {
        await using var h = new Harness(delayMs: 15);
        var id = h.A.SendFile(h.WriteSource("grande.bin", RandomNumberGenerator.GetBytes(3 * 1024 * 1024)));
        await WaitInProgress(h.B, id);

        await h.Pair.DisposeAsync();

        var sent = await WaitFinal(h.A, id);
        Assert.Equal(TransferState.Failed, sent.State);
        Assert.Equal("Conexão perdida.", sent.Error);

        var received = await WaitFinal(h.B, id);
        Assert.Equal(TransferState.Failed, received.State);
        Assert.Equal("Conexão perdida.", received.Error);
        Assert.True(NoPartFiles(h.DstB));
    }

    [Theory]
    [InlineData("fora de ordem")]
    [InlineData("maior que o anunciado")]
    public async Task Chunk_fora_de_ordem_ou_alem_do_Size_falha_a_transferencia(string kind)
    {
        await using var h = new Harness();
        var id = Guid.NewGuid();
        await h.Pair.A.SendAsync(new FileOfferMessage(id, "injetado.bin", 10));
        await WaitInProgress(h.B, id);

        var chunk = kind == "fora de ordem"
            ? new FileChunkMessage(id, 5, new byte[3])
            : new FileChunkMessage(id, 0, new byte[20]);
        await h.Pair.A.SendAsync(chunk);

        var info = await WaitFinal(h.B, id);
        Assert.Equal(TransferState.Failed, info.State);
        Assert.False(string.IsNullOrEmpty(info.Error));
        Assert.True(NoPartFiles(h.DstB));
        Assert.False(File.Exists(Path.Combine(h.DstB, "injetado.bin")));
    }

    [Fact]
    public void SendFile_sem_canal_lanca_InvalidOperationException()
    {
        using var temp = new TempDir();
        using var service = new FileTransferService(temp.File("dst"));
        var path = temp.File("a.txt");
        File.WriteAllText(path, "x");

        Assert.Throws<InvalidOperationException>(() => service.SendFile(path));
    }

    [Fact]
    public async Task TransferChanged_chega_com_estado_final_Completed()
    {
        await using var h = new Harness();
        var changes = new ConcurrentQueue<TransferInfo>();
        h.B.TransferChanged += changes.Enqueue;

        var id = h.A.SendFile(h.WriteSource("evento.txt", Encoding.UTF8.GetBytes("oi")));
        await WaitFinal(h.B, id);

        await Wait.Until(() => changes.Any(i => i.Id == id && i.State == TransferState.Completed), LongWaitMs,
            "evento Completed do destinatário");
        var final = changes.Last(i => i.Id == id);
        Assert.Equal(TransferState.Completed, final.State);
        Assert.Equal(TransferDirection.Incoming, final.Direction);
    }
}
