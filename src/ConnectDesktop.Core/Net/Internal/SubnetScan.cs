// Dono: g01. Funções puras da varredura unicast do DiscoveryService.
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Numerics;

namespace ConnectDesktop.Core.Net.Internal;

internal static class SubnetScan
{
    /// <summary>Prefixo mínimo aceito para a varredura (/22 = até 1022 hosts).</summary>
    internal const int MinPrefix = 22;

    /// <summary>Prefixos a partir deste valor (/31 e /32) não são varridos.</summary>
    internal const int MaxPrefixExclusive = 31;

    /// <summary>
    /// Une os hosts de várias (IP, máscara). Entradas sem máscara ou inválidas são ignoradas uma a uma,
    /// sem derrubar as demais.
    /// </summary>
    internal static IReadOnlyList<IPAddress> HostsOfAll(IEnumerable<(IPAddress Address, IPAddress? Mask)> entries)
    {
        var result = new HashSet<IPAddress>();
        foreach (var (address, mask) in entries)
        {
            if (mask == null)
                continue;
            try
            {
                foreach (var host in HostsFor(address, mask))
                    result.Add(host);
            }
            catch (Exception)
            {
                // Entrada problemática: ignora e segue.
            }
        }
        return result.ToList();
    }

    /// <summary>
    /// Hosts da sub-rede de <paramref name="address"/>/<paramref name="mask"/>, sem o próprio IP,
    /// o endereço de rede e o de broadcast. Lista vazia se a máscara não for contígua, se o prefixo for menor que
    /// <see cref="MinPrefix"/> ou se algum endereço não for IPv4.
    /// </summary>
    internal static IReadOnlyList<IPAddress> HostsFor(IPAddress address, IPAddress mask)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || mask.AddressFamily != AddressFamily.InterNetwork)
            return Array.Empty<IPAddress>();

        var ip = ToUInt(address);
        var m = ToUInt(mask);
        var prefix = BitOperations.LeadingZeroCount(~m);
        // /31 e /32 não têm hosts a varrer (e a conta de capacidade ficaria negativa).
        if (prefix >= MaxPrefixExclusive)
            return Array.Empty<IPAddress>();
        var contiguous = prefix == 0 ? m == 0u : m == uint.MaxValue << (32 - prefix);
        if (!contiguous || prefix < MinPrefix)
            return Array.Empty<IPAddress>();

        var network = ip & m;
        var broadcast = network | ~m;
        var hosts = new List<IPAddress>((int)(broadcast - network) - 1);
        for (var h = network + 1; h < broadcast; h++)
        {
            if (h != ip)
                hosts.Add(FromUInt(h));
        }
        return hosts;
    }

    /// <summary>Faixas privadas: 10/8, 172.16/12 e 192.168/16.</summary>
    internal static bool IsPrivate(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
            return false;
        var b = address.GetAddressBytes();
        return b[0] == 10
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168);
    }

    private static uint ToUInt(IPAddress address) => BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());

    private static IPAddress FromUInt(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return new IPAddress(bytes);
    }
}
