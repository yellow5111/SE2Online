using System;
using System.Collections.Generic;
using System.Globalization;

namespace Y51_SE2Online.Sync;

/// <summary>
/// A join's download from the server, from the transport's own counters: bytes received since the join was accepted
/// (partly reassembled messages included, they count per frame), the size of what is on the way while a large message
/// is in flight (its fragments declare its length), the speed over the last <see cref="WindowMs"/> and the time left.
/// SE2 Online's own netcode sends the world as object batches after the accept, which announces no size.
/// </summary>
public sealed class DownloadMeter
{
	public const long WindowMs = 1500;

	public const long SampleMs = 100;

	private readonly List<(long Ms, long Bytes)> _samples = new();

	private long _base = -1;

	private long _lastMs = long.MinValue;

	public long Received { get; private set; }

	/// <summary>What this download will have come to once the messages in flight are in; 0 when unknown.</summary>
	public long Total { get; private set; }

	public double BytesPerSecond { get; private set; }

	public bool Started => _base >= 0;

	public void Reset()
	{
		_samples.Clear();
		_base = -1;
		_lastMs = long.MinValue;
		Received = Total = 0;
		BytesPerSecond = 0;
	}

	/// <param name="receivedTotal">Everything received from the server on this connection (all channels, per frame).</param>
	/// <param name="inFlightReceived">Bytes of the messages being reassembled that are already in.</param>
	/// <param name="inFlightTotal">Those messages' declared lengths.</param>
	public void Sample(long now, long receivedTotal, long inFlightReceived, long inFlightTotal)
	{
		// A new connection (rejoin) starts its counters again.
		if (_base < 0 || receivedTotal < _base + Received) { Reset(); _base = Math.Max(0, receivedTotal); }
		if (now - _lastMs < SampleMs && _lastMs != long.MinValue) return;
		_lastMs = now;
		Received = Math.Max(0, receivedTotal - _base);
		long remaining = Math.Max(0, inFlightTotal - inFlightReceived);
		Total = remaining > 0 ? Received + remaining : Total > Received ? Total : 0;
		_samples.Add((now, Received));
		// Keep one sample at or past the window's start, so the speed covers the whole window.
		while (_samples.Count > 2 && now - _samples[1].Ms >= WindowMs) _samples.RemoveAt(0);
		var (t0, b0) = _samples[0];
		if (now - t0 >= 250) BytesPerSecond = Math.Max(0, Received - b0) * 1000.0 / (now - t0);
	}

	/// <summary>Milliseconds left at the current speed, or -1 when the size or the speed is unknown.</summary>
	public long EtaMs => Total > Received && BytesPerSecond >= 1024 ? (long)((Total - Received) / BytesPerSecond * 1000) : -1;

	/// <summary>"12.4 / 52.0 MiB · 4.1 MiB/s · 0:09 left", without the parts not known yet.</summary>
	public string Describe()
	{
		string text = Total > 0 ? $"{Mib(Math.Min(Received, Total))} / {Mib(Total)} MiB" : $"{Mib(Received)} MiB";
		if (BytesPerSecond > 0) text += " · " + Speed(BytesPerSecond);
		if (EtaMs >= 0) text += " · " + Clock(EtaMs) + " left";
		return text;
	}

	public static string Mib(long bytes) => (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture);

	public static string Speed(double bytesPerSecond) => bytesPerSecond >= 1048576
		? (bytesPerSecond / 1048576).ToString("0.0", CultureInfo.InvariantCulture) + " MiB/s"
		: (bytesPerSecond / 1024).ToString("0", CultureInfo.InvariantCulture) + " KiB/s";

	public static string Clock(long ms)
	{
		long seconds = (Math.Max(0, ms) + 999) / 1000;
		return seconds >= 3600 ? $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}" : $"{seconds / 60}:{seconds % 60:00}";
	}
}
