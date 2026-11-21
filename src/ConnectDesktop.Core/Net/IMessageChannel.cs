// CONTRATO FIXO (main). Não editar.
using ConnectDesktop.Core.Protocol;

namespace ConnectDesktop.Core.Net;

/// <summary>Canal autenticado e cifrado entre os dois PCs.</summary>
public interface IMessageChannel : IAsyncDisposable
{
    /// <summary>Id e nome do outro PC (vindos do handshake).</summary>
    string RemoteDeviceId { get; }
    string RemoteDeviceName { get; }

    /// <summary>Thread-safe: pode ser chamado de várias threads ao mesmo tempo.</summary>
    Task SendAsync(Message message, CancellationToken ct = default);

    /// <summary>Lê mensagens até o canal fechar (termina a enumeração) ou dar erro (lança IOException).</summary>
    IAsyncEnumerable<Message> ReadAllAsync(CancellationToken ct = default);

    /// <summary>Disparado uma vez quando a conexão cai ou é fechada.</summary>
    event Action? Closed;
}

/// <summary>Identidade deste PC. Id = Guid "N" fixo salvo nas configurações.</summary>
public sealed record DeviceIdentity(string Id, string Name);

public enum DeviceRole { Primary, Secondary }

/// <summary>PC encontrado na rede pela descoberta.</summary>
public sealed record PeerInfo(string Id, string Name, DeviceRole Role, string Address, int TcpPort, string Version, DateTime LastSeenUtc);

public static class NetDefaults
{
    public const int DiscoveryPort = 47800;
    public const int ControlPort = 47801;
    public const string AppTag = "ConnectDesktop/1";
}
