using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Y51_SE2Online.Core;

public sealed class DirectoryLease
{
    public string Id { get; set; } = "";
    public string Token { get; set; } = "";
    /// <summary>The address the directory listed the server at when it registered (0.18.0 directories); "" when it did not say.</summary>
    public string Address { get; set; } = "";
}

public sealed class DirectoryClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _base;
    // Directories before 0.15.3 refuse unknown fields; after one does, the ping location and mod list are left out for
    // an hour, then tried again, so a server picks them up once its directory is updated without restarting.
    private long _extrasAfterMs;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    public DirectoryClient(string service, HttpMessageHandler handler = null)
    {
        ValidateServiceUrl(service);
        _base = new Uri(service.TrimEnd('/') + "/");
        _http = handler == null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(8);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Y51_SE2Online/0.10");
    }

    /// <summary>A publisher whose announce reaches the directory over IPv4, so the IP it sees is the one an IPv4 game port listens on.</summary>
    public static DirectoryClient Ipv4(string service) => new(service, new SocketsHttpHandler
    {
        ConnectCallback = async (context, cancellation) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, System.Net.Sockets.AddressFamily.InterNetwork, cancellation).ConfigureAwait(false);
            var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancellation).ConfigureAwait(false);
                return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        }
    });

    public static void ValidateServiceUrl(string service)
    {
        if (!Uri.TryCreate(service, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback))) throw new FormatException("Directory URL must use HTTPS (HTTP is allowed only for localhost).");
    }

    public async Task<DirectoryListing[]> BrowseAsync(string kind, CancellationToken cancellation = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        cancellation = timeout.Token;
        if (kind is not ("dedicated" or "session")) throw new ArgumentException("Invalid directory kind.");
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_base, "api/v1/listings?kind=" + kind));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var bytes = await ReadBounded(response, cancellation).ConfigureAwait(false);
        var listings = JsonSerializer.Deserialize<DirectoryListing[]>(bytes, Json) ?? Array.Empty<DirectoryListing>();
        if (listings.Length > 4096) throw new InvalidDataException("The directory returned too many listings.");
        return listings.Where(l =>
        {
            try { l.Validate(); return l.Kind == kind && !string.IsNullOrEmpty(l.Id); }
            catch (Exception ex) when (ex is FormatException or NullReferenceException) { return false; }
        }).ToArray();
    }

    public async Task<DirectoryLease> PublishAsync(DirectoryListing listing, DirectoryLease lease, CancellationToken cancellation = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        cancellation = timeout.Token;
        listing.Validate();
        using var request = Request(lease == null ? HttpMethod.Post : HttpMethod.Put, lease);
        var body = JsonSerializer.SerializeToNode(new
        {
            listing.Kind, listing.Name, listing.WorldName, listing.Description, listing.SteamId,
            listing.Address, listing.VirtualPort, listing.Players, listing.MaxPlayers,
            listing.PluginVersion, listing.GameVersion, listing.SyncVersion, listing.FriendsOnly, listing.Tags
        }, Json)!.AsObject();
        // Sent only when configured: the directory locates "auto" servers itself, and directories older than 0.15.2 refuse unknown fields.
        if (Countries.IsCode(listing.Country)) body["country"] = listing.Country;
        bool extras = Environment.TickCount64 >= _extrasAfterMs && (listing.PingLocation.Length > 0 || listing.Mods.Length > 0 || listing.AutoAddressPort > 0);
        if (extras && listing.PingLocation.Length > 0) body["pingLocation"] = listing.PingLocation;
        // Directories before 0.18.0 refuse it and list the Address sent (the server's own guess) instead.
        if (extras && listing.AutoAddressPort > 0) body["autoAddressPort"] = listing.AutoAddressPort;
        if (extras && listing.Mods.Length > 0) body["mods"] = JsonSerializer.SerializeToNode(listing.Mods, Json);
        request.Content = new StringContent(body.ToJsonString(Json), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
        if (extras && response.StatusCode == HttpStatusCode.BadRequest)
        {
            _extrasAfterMs = Environment.TickCount64 + 3600_000;
            return await PublishAsync(listing, lease, cancellation).ConfigureAwait(false);
        }
        if (lease != null && response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized)
            return await PublishAsync(listing, null, cancellation).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (lease != null) return lease;
        var result = JsonSerializer.Deserialize<DirectoryLease>(await ReadBounded(response, cancellation).ConfigureAwait(false), Json);
        if (result == null || result.Id.Length is < 1 or > 128 || result.Token.Length is < 16 or > 256) throw new InvalidDataException("The directory returned an invalid publication token.");
        try { if (!string.IsNullOrEmpty(result.Address)) ServerEndpoint.Parse(result.Address); }
        catch (FormatException) { result.Address = ""; }
        result.Address ??= "";
        return result;
    }

    public async Task RemoveAsync(DirectoryLease lease, CancellationToken cancellation = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        cancellation = timeout.Token;
        if (lease == null) return;
        using var request = Request(HttpMethod.Delete, lease);
        using var response = await _http.SendAsync(request, cancellation).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return;
        response.EnsureSuccessStatusCode();
    }

    private HttpRequestMessage Request(HttpMethod method, DirectoryLease lease)
    {
        var request = new HttpRequestMessage(method, new Uri(_base, "api/v1/listings" + (lease == null ? "" : "/" + Uri.EscapeDataString(lease.Id))));
        if (lease != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", lease.Token);
        return request;
    }

    private static async Task<byte[]> ReadBounded(HttpResponseMessage response, CancellationToken cancellation)
    {
        const int limit = 4 * 1024 * 1024;
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("Directory response is too large.");
        using var stream = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(chunk, cancellation).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + count > limit) throw new InvalidDataException("Directory response is too large.");
            buffer.Write(chunk, 0, count);
        }
        return buffer.ToArray();
    }
    public void Dispose() => _http.Dispose();
}
