// Dono: g01. Assinaturas públicas FIXAS.
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using ConnectDesktop.Core.Net.Internal;

namespace ConnectDesktop.Core.Net;

/// <summary>
/// Descoberta na rede local por broadcast UDP na porta <see cref="NetDefaults.DiscoveryPort"/>.
/// A cada 2 s envia um anúncio JSON {"tag":"ConnectDesktop/1","id","name","role","tcpPort","version"} para 255.255.255.255
/// e para o broadcast de cada interface IPv4 ativa. Ignora os próprios anúncios (mesmo id).
/// A cada 5 s também envia o anúncio em unicast para os hosts das sub-redes IPv4 ativas (prefixo &gt;= 22),
/// para redes em que o roteador não repassa broadcast. Ao ver um id novo, responde na hora em unicast
/// (no máximo 1 resposta por peer a cada 3 s).
/// Peers sem anúncio há mais de 7 s saem da lista.
/// </summary>
public sealed class DiscoveryService : IDisposable
{
    private static readonly TimeSpan AnnounceInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan UnicastInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReplyInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ScanBatchDelay = TimeSpan.FromMilliseconds(50);
    private const int ScanBatches = 20;
    private const int MaxPeers = 64;
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PeerTimeout = TimeSpan.FromSeconds(7);

    private readonly DeviceIdentity _me;
    private readonly string _version;
    private readonly int _tcpPort;
    private readonly object _lock = new();
    private readonly Dictionary<string, PeerInfo> _peers = new();
    private readonly Dictionary<string, DateTime> _lastReplyUtc = new();
    private UdpClient? _udp;
    private Timer? _announceTimer;
    private Timer? _unicastTimer;
    private Timer? _cleanupTimer;
    private volatile bool _disposed;

    public DiscoveryService(DeviceIdentity me, DeviceRole role, string version, int tcpPort = NetDefaults.ControlPort)
    {
        _me = me;
        Role = role;
        _version = version;
        _tcpPort = tcpPort;
    }

    /// <summary>Pode ser alterado com o serviço rodando (o próximo anúncio já usa o novo valor).</summary>
    public DeviceRole Role { get; set; }

    /// <summary>Disparado (em qualquer thread) quando a lista muda: entra, sai ou muda nome/papel.</summary>
    public event Action? PeersChanged;

    public IReadOnlyList<PeerInfo> Peers
    {
        get
        {
            lock (_lock)
            {
                return _peers.Values.ToList();
            }
        }
    }

    public void Start()
    {
        UdpClient udp;
        lock (_lock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(DiscoveryService));
            if (_udp != null)
                return;

            udp = new UdpClient(AddressFamily.InterNetwork);
            try
            {
                udp.ExclusiveAddressUse = false;
                udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                udp.Client.Bind(new IPEndPoint(IPAddress.Any, NetDefaults.DiscoveryPort));
                udp.EnableBroadcast = true;
            }
            catch (Exception)
            {
                udp.Dispose();
                throw;
            }

            DisableConnectionReset(udp);

            _udp = udp;
            _announceTimer = new Timer(_ => Announce(), null, TimeSpan.Zero, AnnounceInterval);
            _unicastTimer = new Timer(_ => UnicastScan(), null, TimeSpan.Zero, UnicastInterval);
            _cleanupTimer = new Timer(_ => Cleanup(), null, CleanupInterval, CleanupInterval);
        }

        _ = Task.Run(() => ReceiveLoopAsync(udp));
    }

    public void Dispose()
    {
        UdpClient? udp;
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            udp = _udp;
            _udp = null;
            _announceTimer?.Dispose();
            _unicastTimer?.Dispose();
            _cleanupTimer?.Dispose();
        }
        udp?.Close();
    }

    /// <summary>
    /// No Windows, um envio para um host sem porta aberta faz o próximo ReceiveAsync falhar com WSAECONNRESET.
    /// Desligar esse comportamento evita perder recepções durante a varredura unicast.
    /// </summary>
    private static void DisableConnectionReset(UdpClient udp)
    {
        if (!OperatingSystem.IsWindows())
            return;
        try
        {
            udp.Client.IOControl((IOControlCode)(-1744830452L), new byte[] { 0 }, null);
        }
        catch (Exception)
        {
            // Sem esse ajuste a descoberta continua funcionando.
        }
    }

    private async Task ReceiveLoopAsync(UdpClient udp)
    {
        while (!_disposed)
        {
            UdpReceiveResult result;
            try
            {
                result = await udp.ReceiveAsync();
            }
            catch (Exception ex)
            {
                if (_disposed)
                    return;
                Debug.WriteLine($"[Discovery] erro ao receber: {ex.Message}");
                await Task.Delay(200);
                continue;
            }

            try
            {
                HandleAnnouncement(result);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Discovery] anúncio inválido: {ex.Message}");
            }
        }
    }

    private void HandleAnnouncement(UdpReceiveResult result)
    {
        using var doc = JsonDocument.Parse(result.Buffer);
        var root = doc.RootElement;

        if (GetString(root, "tag") != NetDefaults.AppTag)
            return;

        var id = GetString(root, "id");
        if (id == null || id == _me.Id)
            return;

        var name = GetString(root, "name") ?? string.Empty;
        var roleText = GetString(root, "role");
        var role = roleText switch
        {
            "Primary" => DeviceRole.Primary,
            "Secondary" => DeviceRole.Secondary,
            _ => (DeviceRole?)null,
        };
        if (role == null)
            return;

        if (!root.TryGetProperty("tcpPort", out var portElement) || portElement.ValueKind != JsonValueKind.Number)
            return;
        var tcpPort = portElement.GetInt32();
        var version = GetString(root, "version") ?? string.Empty;
        var address = result.RemoteEndPoint.Address.ToString();

        var now = DateTime.UtcNow;
        var peer = new PeerInfo(id, name, role.Value, address, tcpPort, version, now);
        bool changed;
        bool isNew;
        bool reply = false;
        lock (_lock)
        {
            isNew = !_peers.ContainsKey(id);
            changed = isNew || !SameExceptTime(_peers[id], peer);
            // Acima do limite de peers, o id novo não gera resposta (protege contra ids forjados).
            var overLimit = isNew && _peers.Count >= MaxPeers;
            _peers[id] = peer;

            if (_peers.Count > MaxPeers)
            {
                var oldest = _peers.Values.OrderBy(p => p.LastSeenUtc).First().Id;
                _peers.Remove(oldest);
            }

            // Id novo (ou que tinha expirado): responde na hora, no máximo 1 resposta por peer a cada 3 s.
            if (isNew && !overLimit && (!_lastReplyUtc.TryGetValue(id, out var last) || now - last >= ReplyInterval))
            {
                _lastReplyUtc[id] = now;
                reply = true;
            }
        }

        if (reply)
            SendQuiet(BuildAnnouncement(), result.RemoteEndPoint);

        if (changed)
            PeersChanged?.Invoke();
    }

    private byte[] BuildAnnouncement() =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            tag = NetDefaults.AppTag,
            id = _me.Id,
            name = _me.Name,
            role = Role == DeviceRole.Primary ? "Primary" : "Secondary",
            tcpPort = _tcpPort,
            version = _version,
        });

    private void Announce()
    {
        try
        {
            var udp = _udp;
            if (_disposed || udp == null)
                return;

            var payload = BuildAnnouncement();

            foreach (var target in BroadcastTargets())
            {
                try
                {
                    udp.Send(payload, payload.Length, new IPEndPoint(target, NetDefaults.DiscoveryPort));
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Discovery] falha ao enviar para {target}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Discovery] falha no anúncio: {ex.Message}");
        }
    }

    /// <summary>Varredura unicast: envia o anúncio para cada host das sub-redes, espalhado por ~1 s.</summary>
    private void UnicastScan()
    {
        try
        {
            if (_disposed)
                return;

            var targets = UnicastTargets();
            if (targets.Count == 0)
                return;

            var payload = BuildAnnouncement();
            var batchSize = Math.Max(1, (targets.Count + ScanBatches - 1) / ScanBatches);
            _ = SpreadSendAsync(payload, targets, batchSize);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Discovery] falha na varredura: {ex.Message}");
        }
    }

    private async Task SpreadSendAsync(byte[] payload, IReadOnlyList<IPAddress> targets, int batchSize)
    {
        for (var i = 0; i < targets.Count && !_disposed; i += batchSize)
        {
            var end = Math.Min(i + batchSize, targets.Count);
            for (var j = i; j < end; j++)
                SendQuiet(payload, new IPEndPoint(targets[j], NetDefaults.DiscoveryPort));
            await Task.Delay(ScanBatchDelay);
        }
    }

    /// <summary>Envio não bloqueante; erros por destino são ignorados sem log.</summary>
    private void SendQuiet(byte[] payload, IPEndPoint target)
    {
        var udp = _udp;
        if (_disposed || udp == null)
            return;
        _ = SendQuietAsync(udp, payload, target);
    }

    private static async Task SendQuietAsync(UdpClient udp, byte[] payload, IPEndPoint target)
    {
        try
        {
            await udp.Client.SendToAsync(new ArraySegment<byte>(payload), SocketFlags.None, target);
        }
        catch (Exception)
        {
            // Host inalcançável ou socket fechado: ignora.
        }
    }

    private void Cleanup()
    {
        try
        {
            bool removed;
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                var stale = _peers.Values.Where(p => now - p.LastSeenUtc > PeerTimeout).Select(p => p.Id).ToList();
                foreach (var id in stale)
                    _peers.Remove(id);
                removed = stale.Count > 0;

                var oldReplies = _lastReplyUtc.Where(kv => now - kv.Value >= ReplyInterval).Select(kv => kv.Key).ToList();
                foreach (var id in oldReplies)
                    _lastReplyUtc.Remove(id);
            }

            if (removed)
                PeersChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Discovery] falha na limpeza: {ex.Message}");
        }
    }

    /// <summary>Hosts de todas as sub-redes IPv4 ativas (não loopback; com gateway ou rede privada).</summary>
    private static IReadOnlyList<IPAddress> UnicastTargets()
    {
        var entries = new List<(IPAddress Address, IPAddress? Mask)>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                var props = nic.GetIPProperties();
                var hasGateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork);
                foreach (var unicast in props.UnicastAddresses)
                {
                    var address = unicast.Address;
                    if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address))
                        continue;
                    if (!hasGateway && !SubnetScan.IsPrivate(address))
                        continue;
                    entries.Add((address, unicast.IPv4Mask));
                }
            }
            catch (Exception)
            {
                // Uma NIC problemática (ex.: VPN /32) não aborta as outras interfaces.
                continue;
            }
        }
        return SubnetScan.HostsOfAll(entries);
    }

    /// <summary>Broadcast global mais o broadcast de cada interface IPv4 ativa (IP | ~máscara).</summary>
    private static IEnumerable<IPAddress> BroadcastTargets()
    {
        var targets = new HashSet<IPAddress> { IPAddress.Broadcast };
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;

            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(unicast.Address))
                    continue;
                var mask = unicast.IPv4Mask;
                if (mask == null)
                    continue;
                targets.Add(BroadcastOf(unicast.Address, mask));
            }
        }
        return targets;
    }

    private static IPAddress BroadcastOf(IPAddress address, IPAddress mask)
    {
        var ip = address.GetAddressBytes();
        var m = mask.GetAddressBytes();
        var result = new byte[4];
        for (var i = 0; i < 4; i++)
            result[i] = (byte)(ip[i] | ~m[i]);
        return new IPAddress(result);
    }

    private static bool SameExceptTime(PeerInfo a, PeerInfo b) =>
        a.Name == b.Name && a.Role == b.Role && a.Address == b.Address && a.TcpPort == b.TcpPort && a.Version == b.Version;

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
