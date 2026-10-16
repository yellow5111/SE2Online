using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Y51_SE2Online.Core;

/// <summary>
/// Live ping to dedicated servers for the server browser. A dedicated server is a Steam game server, which answers
/// A2S_INFO on its query port (game port + 1, see SteamDedicatedSession) without involving the plugin; any answer,
/// the anti-spoofing challenge included, times one round trip. Nothing reaches the server's game connection.
/// </summary>
public sealed class ServerPinger : IDisposable
{
    private static readonly byte[] Query = [0xFF, 0xFF, 0xFF, 0xFF, (byte)'T', .. System.Text.Encoding.ASCII.GetBytes("Source Engine Query"), 0];
    private const int StaleMs = 10000;

    private readonly UdpClient _udp;
    private readonly CancellationTokenSource _stop = new();
    private readonly Func<long> _clock;
    private readonly ConcurrentDictionary<IPEndPoint, (string Address, long SentMs)> _sent = new();
    private readonly ConcurrentDictionary<string, (int Ms, long AtMs)> _rtt = new();
    private readonly ConcurrentDictionary<string, IPEndPoint> _resolved = new();
    private readonly ConcurrentDictionary<string, bool> _resolving = new();

    public ServerPinger(Func<long> clock = null)
    {
        _clock = clock ?? MpClock.NowMs;
        _udp = new UdpClient(AddressFamily.InterNetworkV6);
        _udp.Client.DualMode = true;
        _udp.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
        // Windows reports an ICMP port-unreachable from an earlier send as a failed receive; ignore those.
        if (OperatingSystem.IsWindows()) _udp.Client.IOControl(-1744830452, new byte[4], null);
        _ = Task.Run(Receive);
    }

    /// <summary>The query endpoint of a listed "host:port" or "[v6]:port" address, or null.</summary>
    public static (string Host, int Port)? QueryTarget(string address)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;
        int split = address.LastIndexOf(':');
        if (split <= 0 || !int.TryParse(address.AsSpan(split + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int port) || port is < 1 or > 65535) return null;
        string host = address.Substring(0, split).Trim('[', ']');
        return host.Length == 0 ? null : (host, port == 65535 ? 65534 : port + 1);
    }

    /// <summary>Sends one query to each address; call it every few seconds while the browser shows them.</summary>
    public void Ping(IEnumerable<string> addresses)
    {
        long now = _clock();
        foreach (string address in addresses)
        {
            if (QueryTarget(address) is not var (host, port)) continue;
            if (!_resolved.TryGetValue(address, out var target))
            {
                if (IPAddress.TryParse(host, out var ip)) _resolved[address] = target = new IPEndPoint(Map(ip), port);
                else { Resolve(address, host, port); continue; }
            }
            _sent[target] = (address, now);
            try { _udp.Send(Query, Query.Length, target); }
            catch (SocketException) { }
            catch (ObjectDisposedException) { return; }
        }
    }

    /// <summary>The last measured round trip in ms, or -1 when the server has not answered lately.</summary>
    public int Get(string address) => address != null && _rtt.TryGetValue(address, out var r) && _clock() - r.AtMs < StaleMs ? r.Ms : -1;

    private void Resolve(string address, string host, int port)
    {
        if (!_resolving.TryAdd(address, true)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var ips = await Dns.GetHostAddressesAsync(host, _stop.Token).ConfigureAwait(false);
                if (ips.Length > 0) _resolved[address] = new IPEndPoint(Map(ips[0]), port);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException) { }
            finally { _resolving.TryRemove(address, out _); }
        });
    }

    private static IPAddress Map(IPAddress ip) => ip.AddressFamily == AddressFamily.InterNetwork ? ip.MapToIPv6() : ip;

    private async Task Receive()
    {
        while (!_stop.IsCancellationRequested)
        {
            UdpReceiveResult reply;
            try { reply = await _udp.ReceiveAsync(_stop.Token).ConfigureAwait(false); }
            catch (SocketException) { continue; }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { return; }
            var from = new IPEndPoint(Map(reply.RemoteEndPoint.Address), reply.RemoteEndPoint.Port);
            if (reply.Buffer.Length < 5 || reply.Buffer[0] != 0xFF || !_sent.TryRemove(from, out var sent)) continue;
            _rtt[sent.Address] = ((int)Math.Max(1, _clock() - sent.SentMs), _clock());
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _udp.Dispose();
    }
}
