using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Y51_SE2Online.Core;

/// <summary>
/// The address a dedicated server is listed at in the server browser. An address the host typed (Network.PublicAddress)
/// always wins. Otherwise the directory lists the IP it sees the announce come from, which is right behind NAT too; the
/// server still sends its own best guess for directories that do not do that yet: the address the directory reported
/// before, then Steam's public IP for the game server, then a public IP on one of this machine's network cards.
/// Co-op sessions are joined through Steam, so they list no address unless the host typed one.
/// </summary>
public static class ListedAddress
{
    public static string Choose(string typed, string observed, IPAddress steam, IEnumerable<IPAddress> local, int port)
    {
        if (!string.IsNullOrWhiteSpace(typed)) return typed.Trim();
        if (!string.IsNullOrWhiteSpace(observed)) return observed.Trim();
        IPAddress ip = IsPublic(steam) ? steam : (local ?? Enumerable.Empty<IPAddress>()).Where(IsPublic)
            .OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).FirstOrDefault();
        return ip == null ? "" : Endpoint(ip, port);
    }

    public static string Endpoint(IPAddress ip, int port)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        return (ip.AddressFamily == AddressFamily.InterNetworkV6 ? "[" + ip + "]" : ip.ToString()) + ":" + port.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The same test as the directory's publicIp(): an address it would refuse (private, loopback, CGNAT, documentation...) is never guessed.</summary>
    public static bool IsPublic(IPAddress ip)
    {
        if (ip == null) return false;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        byte[] b = ip.GetAddressBytes();
        if (b.Length == 16) return b[0] >= 0x20 && b[0] <= 0x3f && !(b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8);
        return b[0] is not (0 or 10 or 127) && b[0] < 224 && !(b[0] == 169 && b[1] == 254)
            && !(b[0] == 172 && b[1] >= 16 && b[1] <= 31) && !(b[0] == 192 && b[1] is 0 or 168)
            && !(b[0] == 100 && b[1] >= 64 && b[1] <= 127) && !(b[0] == 198 && b[1] is 18 or 19)
            && !(b[0] == 198 && b[1] == 51 && b[2] == 100) && !(b[0] == 203 && b[1] == 0 && b[2] == 113);
    }

    private static IPAddress[] _local;

    // ponytail: read once per process; a server whose network card gets a new public IP lists it after a restart (the directory's own view needs none).
    public static IReadOnlyList<IPAddress> Local()
    {
        if (_local != null) return _local;
        try
        {
            _local = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address).ToArray();
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException) { _local = Array.Empty<IPAddress>(); }
        return _local;
    }
}
