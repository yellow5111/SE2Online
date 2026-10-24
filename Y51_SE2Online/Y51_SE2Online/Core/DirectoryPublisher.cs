using System;
using System.Threading.Tasks;

namespace Y51_SE2Online.Core;

public sealed class DirectoryPublisher
{
    private readonly DirectoryClient _client;
    private readonly Action<string> _error;
    private DirectoryLease _lease;
    private Task _flight = Task.CompletedTask;
    private long _next;
    private bool _stopping;
    public string Error { get; private set; }
    /// <summary>The address the directory listed this server at when it registered; "" before that or from older directories.</summary>
    public string ObservedAddress => _lease?.Address ?? "";

    public DirectoryPublisher(DirectoryClient client, Action<string> error) { _client = client; _error = error; }
    public void Tick(DirectoryListing listing, long now)
    {
        if (_stopping || !_flight.IsCompleted || now < _next) return;
        _next = now + 30000;
        _flight = Task.Run(() => Publish(listing));
    }
    private async Task Publish(DirectoryListing listing)
    {
        try
        {
            if (listing == null) { await _client.RemoveAsync(_lease).ConfigureAwait(false); _lease = null; }
            else _lease = await _client.PublishAsync(listing, _lease).ConfigureAwait(false);
            Error = null;
        }
        catch (Exception ex) { Error = ex.Message; _error?.Invoke("Directory listing unavailable: " + ex.Message); }
    }
    public async Task StopAsync()
    {
        _stopping = true;
        await _flight.ConfigureAwait(false);
        try { await _client.RemoveAsync(_lease).ConfigureAwait(false); }
        catch (Exception ex) { _error?.Invoke("Directory lease will expire: " + ex.Message); }
        finally { _client.Dispose(); }
    }
}
