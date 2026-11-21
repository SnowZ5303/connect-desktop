// Dono: g01. Assinaturas públicas FIXAS.
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using ConnectDesktop.Core.Protocol;
using ConnectDesktop.Core.Security;

namespace ConnectDesktop.Core.Net;

/// <summary>Implementação de <see cref="IMessageChannel"/> sobre TCP com handshake ECDH + código de pareamento.</summary>
public sealed class SecureChannel : IMessageChannel
{
    private const int FrameOverhead = 64;
    private const int CounterSize = 8;
    private const int TagSize = 16;
    private const int NonceSize = 12;
    private const int KeySize = 32;
    private const int NonceRandomSize = 16;
    private const string ModeKey = "key";
    private const string ModeCode = "code";
    private const string MsgNotRecognized = "Este PC não reconhece mais o pareamento. Pareie de novo.";
    private const string MsgWrongCode = "Código de pareamento incorreto.";
    private const string MsgNotConfirmed = "O outro PC não confirmou a identidade.";
    private const string MsgCancelled = "Pareamento cancelado.";
    private const string MsgUnknownPairing = "O outro PC não reconhece este pareamento. Esqueça o pareamento em 'Pares salvos' e conecte de novo para parear com código.";

    /// <summary>Limite de cada etapa de rede do handshake (a espera do código digitado não entra).</summary>
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Limite do secundário para esperar o ClientAuth quando não conhece o principal (o código pode estar sendo digitado).</summary>
    private static readonly TimeSpan PairingInputTimeout = TimeSpan.FromSeconds(120);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private TcpClient _client = null!;
    private NetworkStream _stream = null!;
    private string _remoteId = string.Empty;
    private string _remoteName = string.Empty;
    private AesGcm? _sendAes;
    private AesGcm? _recvAes;
    private ulong _sendCounter;
    private ulong _lastRecvCounter;
    private int _closedRaised;
    private volatile bool _disposed;

    private SecureChannel() { }

    public string RemoteDeviceId => _remoteId;
    public string RemoteDeviceName => _remoteName;
    public event Action? Closed;

    /// <summary>
    /// Lado principal: conecta em host:port e faz o handshake.
    /// <paramref name="askPairingCode"/> é chamado só se o secundário ainda não estiver pareado; retorna o código digitado ou null (cancelar).
    /// Lança <see cref="PairingException"/> se o código estiver errado ou o pareamento for recusado/cancelado.
    /// </summary>
    public static Task<SecureChannel> ConnectAsync(string host, int port, DeviceIdentity me, PairingStore store,
        Func<Task<string?>> askPairingCode, CancellationToken ct = default)
    {
        var client = new TcpClient();
        return RunHandshakeAsync(client, ct, async clock =>
        {
            await client.ConnectAsync(host, port, clock.Start(StepTimeout));
            client.NoDelay = true;
            var stream = client.GetStream();

            using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var clientHello = BuildClientHello(me, ecdh.ExportSubjectPublicKeyInfo());
            await WriteFrameAsync(stream, clientHello, clock.Start(StepTimeout));

            var serverHelloBody = await ReadFrameRequiredAsync(stream, clock.Start(StepTimeout));
            var server = ParseHello(serverHelloBody);
            CheckTag(server.Tag);

            var secret = SharedSecret(ecdh, server.Pub);
            var transcript = Transcript(clientHello, serverHelloBody);

            string mode;
            byte[] authKey;
            var storedServerKey = store.GetKey(server.Id);
            if (storedServerKey != null && server.KnowsClient)
            {
                mode = ModeKey;
                authKey = storedServerKey;
            }
            else
            {
                // A espera pelo código digitado não conta no relógio do handshake.
                clock.Pause();
                var code = await askPairingCode();
                if (code == null)
                    throw new PairingException(MsgCancelled);
                mode = ModeCode;
                authKey = CodeKey(code);
            }

            var clientAuth = JsonSerializer.SerializeToUtf8Bytes(new
            {
                mode,
                mac = Convert.ToBase64String(Mac(authKey, "client", transcript, secret)),
            }, JsonOptions);
            await WriteFrameAsync(stream, clientAuth, clock.Start(StepTimeout));

            var serverAuthBody = await ReadFrameRequiredAsync(stream, clock.Start(StepTimeout));
            var (ok, error, serverMac) = ParseServerAuth(serverAuthBody);
            if (!ok)
            {
                // No modo key a chave nunca é apagada automaticamente: um servidor não autenticado poderia forçar a exclusão.
                if (mode == ModeKey)
                    throw new PairingException(MsgUnknownPairing);
                throw new PairingException(string.IsNullOrEmpty(error) ? "Pareamento recusado pelo outro PC." : error);
            }
            if (serverMac == null || !CryptographicOperations.FixedTimeEquals(serverMac, Mac(authKey, "server", transcript, secret)))
                throw new PairingException(MsgNotConfirmed);

            if (mode == ModeCode)
                store.SetKey(server.Id, server.Name, DeriveKey(secret, transcript, "cd-pair"));

            var kC2S = DeriveKey(secret, transcript, "cd-c2s");
            var kS2C = DeriveKey(secret, transcript, "cd-s2c");
            return Create(client, server.Id, server.Name, sendKey: kC2S, recvKey: kS2C);
        });
    }

    /// <summary>
    /// Lado secundário: faz o handshake num cliente já aceito.
    /// <paramref name="currentPairingCode"/> devolve o código de 6 dígitos que está aparecendo na tela agora.
    /// </summary>
    public static Task<SecureChannel> AcceptAsync(TcpClient client, DeviceIdentity me, PairingStore store,
        Func<string> currentPairingCode, CancellationToken ct = default)
    {
        return RunHandshakeAsync(client, ct, async clock =>
        {
            client.NoDelay = true;
            var stream = client.GetStream();

            var clientHelloBody = await ReadFrameRequiredAsync(stream, clock.Start(StepTimeout));
            var peerHello = ParseHello(clientHelloBody);
            CheckTag(peerHello.Tag);

            using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var knowsPeer = store.GetKey(peerHello.Id) != null;
            var serverHello = BuildServerHello(me, ecdh.ExportSubjectPublicKeyInfo(), knowsPeer);
            await WriteFrameAsync(stream, serverHello, clock.Start(StepTimeout));

            var secret = SharedSecret(ecdh, peerHello.Pub);
            var transcript = Transcript(clientHelloBody, serverHello);

            // Sem chave guardada, o principal pode estar digitando o código: espera mais.
            var clientAuthBody = await ReadFrameRequiredAsync(stream,
                clock.Start(knowsPeer ? StepTimeout : PairingInputTimeout));
            var (mode, clientMac) = ParseClientAuth(clientAuthBody);

            byte[] authKey;
            string failMessage;
            if (mode == ModeKey)
            {
                failMessage = MsgNotRecognized;
                var storedKey = store.GetKey(peerHello.Id);
                if (storedKey == null)
                {
                    await TrySendFailAsync(stream, failMessage, clock.Start(StepTimeout));
                    throw new PairingException(failMessage);
                }
                authKey = storedKey;
            }
            else if (mode == ModeCode)
            {
                failMessage = MsgWrongCode;
                authKey = CodeKey(currentPairingCode());
            }
            else
            {
                throw new IOException("Modo de pareamento desconhecido.");
            }

            if (!CryptographicOperations.FixedTimeEquals(clientMac, Mac(authKey, "client", transcript, secret)))
            {
                await TrySendFailAsync(stream, failMessage, clock.Start(StepTimeout));
                throw new PairingException(failMessage);
            }

            var serverAuth = JsonSerializer.SerializeToUtf8Bytes(new
            {
                ok = true,
                mac = Convert.ToBase64String(Mac(authKey, "server", transcript, secret)),
            }, JsonOptions);
            await WriteFrameAsync(stream, serverAuth, clock.Start(StepTimeout));

            if (mode == ModeCode)
                store.SetKey(peerHello.Id, peerHello.Name, DeriveKey(secret, transcript, "cd-pair"));

            var kC2S = DeriveKey(secret, transcript, "cd-c2s");
            var kS2C = DeriveKey(secret, transcript, "cd-s2c");
            return Create(client, peerHello.Id, peerHello.Name, sendKey: kS2C, recvKey: kC2S);
        });
    }

    public async Task SendAsync(Message message, CancellationToken ct = default)
    {
        // Erro de tamanho/encode não é falha de rede: não fecha o canal.
        var plaintext = MessageCodec.Encode(message);
        if (IsClosed)
            throw new IOException("Canal fechado.");

        await _sendLock.WaitAsync(ct);
        try
        {
            var counter = ++_sendCounter;
            var body = new byte[CounterSize + plaintext.Length + TagSize];
            BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(0, CounterSize), counter);
            _sendAes!.Encrypt(MakeNonce(counter), plaintext,
                body.AsSpan(CounterSize, plaintext.Length),
                body.AsSpan(CounterSize + plaintext.Length, TagSize));
            await WriteFrameAsync(_stream, body, ct);
        }
        catch (OperationCanceledException)
        {
            // Cancelamento no meio de um quadro deixa o fluxo corrompido: fecha o canal.
            Fail();
            throw;
        }
        catch (Exception ex)
        {
            Fail();
            throw new IOException("Falha ao enviar para o outro PC.", ex);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async IAsyncEnumerable<Message> ReadAllAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        while (true)
        {
            Message? message = null;
            Exception? failure = null;
            bool ended = false;
            try
            {
                var body = await ReadFrameAsync(_stream, ct);
                if (body == null)
                    ended = true;
                else
                    message = DecryptAndDecode(body);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            if (failure != null)
            {
                if (_disposed)
                    yield break;
                Fail();
                throw new IOException("Conexão perdida com o outro PC.", failure);
            }

            if (ended)
            {
                Fail();
                yield break;
            }

            yield return message!;
        }
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        CloseSocket();
        RaiseClosed();
        _sendAes?.Dispose();
        _recvAes?.Dispose();
        return ValueTask.CompletedTask;
    }

    private bool IsClosed => Volatile.Read(ref _closedRaised) != 0;

    private void Fail()
    {
        CloseSocket();
        RaiseClosed();
    }

    private void CloseSocket()
    {
        try
        {
            _client?.Close();
        }
        catch (Exception)
        {
            // Já fechado: ignora.
        }
    }

    private void RaiseClosed()
    {
        if (Interlocked.Exchange(ref _closedRaised, 1) == 0)
            Closed?.Invoke();
    }

    private Message DecryptAndDecode(byte[] body)
    {
        if (body.Length < CounterSize + TagSize)
            throw new IOException("Quadro cifrado inválido.");

        var counter = BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(0, CounterSize));
        if (counter <= _lastRecvCounter)
            throw new IOException("Quadro fora de ordem ou repetido.");

        var cipherLength = body.Length - CounterSize - TagSize;
        var plaintext = new byte[cipherLength];
        _recvAes!.Decrypt(MakeNonce(counter),
            body.AsSpan(CounterSize, cipherLength),
            body.AsSpan(CounterSize + cipherLength, TagSize),
            plaintext);
        _lastRecvCounter = counter;
        return MessageCodec.Decode(plaintext);
    }

    private static byte[] MakeNonce(ulong counter)
    {
        var nonce = new byte[NonceSize];
        BinaryPrimitives.WriteUInt64LittleEndian(nonce.AsSpan(4, CounterSize), counter);
        return nonce;
    }

    private static SecureChannel Create(TcpClient client, string remoteId, string remoteName, byte[] sendKey, byte[] recvKey)
    {
        return new SecureChannel
        {
            _client = client,
            _stream = client.GetStream(),
            _remoteId = remoteId,
            _remoteName = remoteName,
            _sendAes = new AesGcm(sendKey, TagSize),
            _recvAes = new AesGcm(recvKey, TagSize),
        };
    }

    /// <summary>
    /// Relógio do handshake: cada etapa de rede ganha o seu limite (<see cref="Start"/>).
    /// <see cref="Pause"/> tira o relógio do ar (ex.: enquanto o usuário digita o código).
    /// </summary>
    private sealed class HandshakeClock : IDisposable
    {
        private readonly CancellationToken _outer;
        private CancellationTokenSource? _step;

        public HandshakeClock(CancellationToken outer) => _outer = outer;

        public CancellationToken Start(TimeSpan limit)
        {
            _step?.Dispose();
            _step = CancellationTokenSource.CreateLinkedTokenSource(_outer);
            _step.CancelAfter(limit);
            return _step.Token;
        }

        public void Pause()
        {
            _step?.Dispose();
            _step = null;
        }

        public void Dispose() => Pause();
    }

    /// <summary>Aplica os limites de cada etapa ao handshake e converte qualquer falha em IOException/PairingException.</summary>
    private static async Task<SecureChannel> RunHandshakeAsync(TcpClient client, CancellationToken ct,
        Func<HandshakeClock, Task<SecureChannel>> body)
    {
        using var clock = new HandshakeClock(ct);
        try
        {
            return await body(clock);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            client.Close();
            throw new IOException("Tempo esgotado no handshake com o outro PC.");
        }
        catch (OperationCanceledException)
        {
            client.Close();
            throw;
        }
        catch (PairingException)
        {
            client.Close();
            throw;
        }
        catch (IOException)
        {
            client.Close();
            throw;
        }
        catch (Exception ex)
        {
            client.Close();
            throw new IOException("Falha no handshake com o outro PC.", ex);
        }
    }

    private static async Task WriteFrameAsync(Stream stream, byte[] body, CancellationToken ct)
    {
        if (body.Length > MessageCodec.MaxMessageSize + FrameOverhead)
            throw new ArgumentException("Quadro grande demais.");

        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(0, 4), (uint)body.Length);
        body.CopyTo(frame, 4);
        await stream.WriteAsync(frame, ct);
    }

    /// <summary>Lê um quadro. Devolve null só se a conexão fechar exatamente antes de um quadro.</summary>
    private static async Task<byte[]?> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        if (!await ReadExactAsync(stream, header, ct, allowEofAtStart: true))
            return null;

        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length > MessageCodec.MaxMessageSize + FrameOverhead)
            throw new IOException("Quadro recebido grande demais.");

        var body = new byte[length];
        await ReadExactAsync(stream, body, ct, allowEofAtStart: false);
        return body;
    }

    private static async Task<byte[]> ReadFrameRequiredAsync(Stream stream, CancellationToken ct)
    {
        return await ReadFrameAsync(stream, ct) ?? throw new IOException("Conexão encerrada durante o handshake.");
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct, bool allowEofAtStart)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), ct);
            if (read == 0)
            {
                if (offset == 0 && allowEofAtStart)
                    return false;
                throw new IOException("Conexão encerrada pelo outro PC.");
            }
            offset += read;
        }
        return true;
    }

    private static async Task TrySendFailAsync(Stream stream, string message, CancellationToken ct)
    {
        try
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(new { ok = false, error = message }, JsonOptions);
            await WriteFrameAsync(stream, body, ct);
        }
        catch (Exception)
        {
            // A conexão já pode estar caída: o erro de pareamento é lançado mesmo assim.
        }
    }

    private sealed record HelloInfo(string Tag, string Id, string Name, string Pub, bool KnowsClient);

    private static byte[] BuildClientHello(DeviceIdentity me, byte[] publicKey) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            tag = NetDefaults.AppTag,
            id = me.Id,
            name = me.Name,
            pub = Convert.ToBase64String(publicKey),
            nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(NonceRandomSize)),
        }, JsonOptions);

    private static byte[] BuildServerHello(DeviceIdentity me, byte[] publicKey, bool knowsClient) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            tag = NetDefaults.AppTag,
            id = me.Id,
            name = me.Name,
            pub = Convert.ToBase64String(publicKey),
            nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(NonceRandomSize)),
            knowsClient,
        }, JsonOptions);

    private static HelloInfo ParseHello(byte[] body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var knows = root.TryGetProperty("knowsClient", out var k) && k.ValueKind == JsonValueKind.True;
        return new HelloInfo(GetString(root, "tag"), GetString(root, "id"), GetString(root, "name"),
                             GetString(root, "pub"), knows);
    }

    private static (string Mode, byte[] Mac) ParseClientAuth(byte[] body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var mode = GetString(root, "mode");
        var mac = Convert.FromBase64String(GetString(root, "mac"));
        return (mode, mac);
    }

    private static (bool Ok, string? Error, byte[]? Mac) ParseServerAuth(byte[] body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var ok = root.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
        string? error = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        byte[]? mac = root.TryGetProperty("mac", out var m) && m.ValueKind == JsonValueKind.String
            ? Convert.FromBase64String(m.GetString()!)
            : null;
        return (ok, error, mac);
    }

    private static string GetString(JsonElement root, string name)
    {
        if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            return value.GetString() ?? throw new IOException($"Handshake inválido: campo '{name}' vazio.");
        throw new IOException($"Handshake inválido: campo '{name}' ausente.");
    }

    private static void CheckTag(string tag)
    {
        if (tag != NetDefaults.AppTag)
            throw new IOException("O outro PC não é um Connect Desktop compatível.");
    }

    private static byte[] Transcript(byte[] clientHello, byte[] serverHello)
    {
        return SHA256.HashData(Concat(clientHello, serverHello));
    }

    private static byte[] SharedSecret(ECDiffieHellman local, string peerPublicKeyBase64)
    {
        using var peer = ECDiffieHellman.Create();
        peer.ImportSubjectPublicKeyInfo(Convert.FromBase64String(peerPublicKeyBase64), out _);
        return local.DeriveRawSecretAgreement(peer.PublicKey);
    }

    private static byte[] CodeKey(string code)
    {
        return SHA256.HashData(Encoding.UTF8.GetBytes("CD-PAIR:" + code.Trim()));
    }

    private static byte[] Mac(byte[] key, string label, byte[] transcript, byte[] secret)
    {
        return HMACSHA256.HashData(key, Concat(Encoding.UTF8.GetBytes(label), transcript, secret));
    }

    private static byte[] DeriveKey(byte[] secret, byte[] transcript, string info)
    {
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, KeySize, transcript, Encoding.UTF8.GetBytes(info));
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var total = 0;
        foreach (var part in parts)
            total += part.Length;

        var result = new byte[total];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }
        return result;
    }
}
