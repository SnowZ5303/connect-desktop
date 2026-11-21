// Dono: g01. Assinaturas públicas FIXAS.
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using ConnectDesktop.Core.Security;

namespace ConnectDesktop.Core.Net;

/// <summary>Servidor TCP do PC secundário. Aceita um principal por vez (uma nova conexão válida derruba a anterior).</summary>
public sealed class ControlListener : IAsyncDisposable
{
    /// <summary>Máximo de handshakes pendentes ao mesmo tempo. Acima disso a conexão nova é fechada sem handshake.</summary>
    private const int MaxPendingHandshakes = 4;

    private readonly DeviceIdentity _me;
    private readonly PairingStore _store;
    private readonly Func<string> _currentPairingCode;
    private readonly int _requestedPort;
    private readonly object _lock = new();
    // handshake pendente -> IP remoto dele
    private readonly Dictionary<TcpClient, string> _pending = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private IMessageChannel? _current;
    private int _port;
    private bool _disposed;

    public ControlListener(DeviceIdentity me, PairingStore store, Func<string> currentPairingCode, int port = NetDefaults.ControlPort)
    {
        _me = me;
        _store = store;
        _currentPairingCode = currentPairingCode;
        _requestedPort = port;
        _port = port;
    }

    /// <summary>Porta real em uso (útil quando port = 0 nos testes).</summary>
    public int Port => _port;

    /// <summary>Disparado após um handshake bem-sucedido.</summary>
    public event Action<IMessageChannel>? ChannelAccepted;
    /// <summary>Disparado quando um handshake falha (ex.: código errado). Texto em português.</summary>
    public event Action<string>? HandshakeFailed;

    public void Start()
    {
        lock (_lock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(ControlListener));
            if (_listener != null)
                return;

            var listener = new TcpListener(IPAddress.Any, _requestedPort);
            listener.Start();
            _listener = listener;
            _port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _acceptLoop = Task.Run(() => AcceptLoopAsync(listener, token));
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? loop;
        IMessageChannel? channel;
        List<TcpClient> pending;
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            _cts?.Cancel();
            _listener?.Stop();
            loop = _acceptLoop;
            channel = _current;
            _current = null;
            pending = _pending.Keys.ToList();
            _pending.Clear();
        }

        foreach (var client in pending)
            client.Close();

        if (channel != null)
            await channel.DisposeAsync();

        if (loop != null)
        {
            try
            {
                await loop;
            }
            catch (Exception)
            {
                // Loop já encerrado: ignora.
            }
        }

        _cts?.Dispose();
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                // Erro momentâneo ao aceitar: espera um pouco e continua.
                try
                {
                    await Task.Delay(200, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                continue;
            }

            var ip = RemoteIp(client);
            bool accepted;
            bool disposed;
            TcpClient? stale = null;
            lock (_lock)
            {
                disposed = _disposed;
                if (!disposed)
                {
                    // No máximo 1 handshake pendente por IP: o pendente antigo do mesmo IP é fechado e o novo entra
                    // (se o principal caiu no meio do handshake e reconectou, não fica bloqueado).
                    var older = _pending.FirstOrDefault(kv => kv.Value == ip);
                    if (older.Key != null)
                    {
                        stale = older.Key;
                        _pending.Remove(stale);
                    }

                    // Limite global: 4 handshakes pendentes ao mesmo tempo.
                    accepted = _pending.Count < MaxPendingHandshakes;
                    if (accepted)
                        _pending[client] = ip;
                }
                else
                {
                    accepted = false;
                }
            }

            stale?.Close();

            if (disposed)
            {
                client.Close();
                break;
            }
            if (!accepted)
            {
                client.Close();
                continue;
            }

            _ = Task.Run(() => HandleClientAsync(client, ct));
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        SecureChannel channel;
        try
        {
            channel = await SecureChannel.AcceptAsync(client, _me, _store, _currentPairingCode, ct);
        }
        catch (Exception ex) when (ex is PairingException or IOException)
        {
            client.Close();
            RemovePending(client);
            RaiseHandshakeFailed(ex.Message);
            return;
        }
        catch (Exception)
        {
            client.Close();
            RemovePending(client);
            RaiseHandshakeFailed("Falha ao conectar com o outro PC.");
            return;
        }

        RemovePending(client);

        bool disposed;
        IMessageChannel? previous = null;
        lock (_lock)
        {
            disposed = _disposed;
            if (!disposed)
            {
                // Closed é registrado antes de publicar _current.
                channel.Closed += () =>
                {
                    lock (_lock)
                    {
                        if (ReferenceEquals(_current, channel))
                            _current = null;
                    }
                };
                previous = _current;
                _current = channel;
            }
        }

        if (disposed)
        {
            await channel.DisposeAsync();
            return;
        }

        if (previous != null)
            await previous.DisposeAsync();

        // Exceção do assinante não pode cair no tratamento de handshake.
        try
        {
            ChannelAccepted?.Invoke(channel);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ControlListener] assinante de ChannelAccepted lançou: {ex.Message}");
        }
    }

    private void RemovePending(TcpClient client)
    {
        lock (_lock)
        {
            _pending.Remove(client);
        }
    }

    private void RaiseHandshakeFailed(string message)
    {
        lock (_lock)
        {
            if (_disposed)
                return;
        }
        HandshakeFailed?.Invoke(message);
    }

    private static string RemoteIp(TcpClient client)
    {
        try
        {
            return ((IPEndPoint?)client.Client.RemoteEndPoint)?.Address.ToString() ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }
}
