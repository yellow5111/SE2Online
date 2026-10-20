using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using Keen.VRage.Core.Services.Data;
using Keen.VRage.Core.Services.GameSessions;
using Keen.VRage.Core.Services.Networking;
using Y51_SE2Online.Core;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>A game on this PC reached over loopback TCP instead of Steam (Steam cannot connect an account to itself).</summary>
internal sealed record LocalSessionID(IPEndPoint Endpoint) : GameSessionID;

/// <summary>
/// Test play with several game instances on one Steam account (SE2 4.0's netcode runs over Steam P2P, which refuses a
/// connection to the same account). The server takes local players on a loopback TCP listener next to its Steam
/// session; each gets its own player (a PeerAlias id and a game user of its own), so its identity, character and
/// SE2 Online record are separate from the account's other instances.
/// </summary>
internal static class LocalPeers
{
	/// <summary>The server, as local players' sessions name it.</summary>
	public static readonly GameServiceUser Server = new(new Guid("7f0e3c55-51e2-4c1a-9b1e-5e2a0d1c0001"));

	public static GameServiceUser UserFor(int slot) => new(new Guid(0x7f0e3c55, 0x51e2, 0x4c1a, 0x9b, 0x1e, 0x5e, 0x2a, 0x0d, 0x1c, 0x01, (byte)slot));

	private static readonly byte[] Template = UserFor(0).Id.ToByteArray();

	public static int SlotOf(GameServiceUser user)
	{
		var b = user.Id.ToByteArray();
		for (int i = 0; i < 15; i++) if (b[i] != Template[i]) return 0;
		return b[15];
	}

	/// <summary>Player ids of the extra local instances (PeerAlias of the account they run under), by their game user.</summary>
	private static readonly ConcurrentDictionary<GameServiceUser, ulong> Aliases = new();

	/// <summary>The player id SE2 Online uses for an extra local instance of an account.</summary>
	public static ulong? SteamIdOf(GameServiceUser user) => Aliases.TryGetValue(user, out ulong id) ? id : null;

	public static GameServiceUser? UserOf(ulong id)
	{
		foreach (var (user, alias) in Aliases) if (alias == id) return user;
		return null;
	}

	internal static void SetAlias(GameServiceUser user, ulong alias) => Aliases[user] = alias;

	internal static void ClearAlias(GameServiceUser user) => Aliases.TryRemove(user, out _);

	internal const int HeaderBytes = 6;

	internal static void WriteFrame(Stream stream, ReadOnlySpan<byte> data, byte channel, MessageReliability reliability)
	{
		Span<byte> header = stackalloc byte[HeaderBytes];
		BitConverter.TryWriteBytes(header, data.Length);
		header[4] = channel;
		header[5] = (byte)reliability;
		lock (stream)
		{
			stream.Write(header);
			stream.Write(data);
		}
	}

	internal static bool ReadFrame(Stream stream, out byte channel, out MessageReliability reliability, out byte[] data)
	{
		Span<byte> header = stackalloc byte[HeaderBytes];
		channel = 0;
		reliability = default;
		data = Array.Empty<byte>();
		if (!ReadExactly(stream, header)) return false;
		int length = BitConverter.ToInt32(header);
		if (length < 0 || length > 64 << 20) return false;
		channel = header[4];
		reliability = (MessageReliability)header[5];
		data = new byte[length];
		return ReadExactly(stream, data);
	}

	private static bool ReadExactly(Stream stream, Span<byte> buffer)
	{
		int read = 0;
		while (read < buffer.Length)
		{
			int n = stream.Read(buffer.Slice(read));
			if (n <= 0) return false;
			read += n;
		}
		return true;
	}
}

/// <summary>Messages received from local peers, handed to the game as <see cref="IPeerToPeerSession.Message"/>s.</summary>
internal abstract class LocalMessageSource
{
	private readonly ConcurrentDictionary<byte, ConcurrentQueue<(GameServiceUser From, MessageReliability Reliability, byte[] Data)>> _inbox = new();

	protected void Enqueue(GameServiceUser from, byte channel, MessageReliability reliability, byte[] data)
		=> _inbox.GetOrAdd(channel, _ => new()).Enqueue((from, reliability, data));

	protected bool TryDequeue(IPeerToPeerSession session, byte channel, out IPeerToPeerSession.Message message)
	{
		message = default;
		if (!_inbox.TryGetValue(channel, out var queue) || !queue.TryDequeue(out var item)) return false;
		nint buffer = Marshal.AllocHGlobal(Math.Max(1, item.Data.Length));
		Marshal.Copy(item.Data, 0, buffer, item.Data.Length);
		message = Create(item.From, buffer, item.Data.Length, item.Reliability, session);
		return true;
	}

	protected abstract IPeerToPeerSession.Message Create(GameServiceUser from, nint buffer, int length, MessageReliability reliability, IPeerToPeerSession session);

	protected static void Free(nint handle) => Marshal.FreeHGlobal(handle);
}

/// <summary>Server side: Keen's own Steam session plus local players on loopback TCP and direct IP players.</summary>
internal sealed class LocalServerSession : LocalMessageSource, IPeerToPeerSession
{
	private readonly IPeerToPeerSession _inner;

	private readonly TcpListener? _listener;

	private readonly DirectIpPeers? _direct;

	private readonly IMpLog _log;

	private readonly ConcurrentDictionary<GameServiceUser, NetworkStream> _peers = new();

	private readonly ConcurrentQueue<(GameServiceUser User, bool Connected)> _changes = new();

	private Action<GameServiceUser>? _requested;

	private Action<GameServiceUser, ConnectionFailureReason>? _failed;

	private volatile bool _disposed;

	public IPeerToPeerSession Inner => _inner;

	public LocalServerSession(IPeerToPeerSession inner, int port, DirectIpPeers? direct, IMpLog log)
	{
		_inner = inner;
		_log = log;
		_direct = direct;
		try
		{
			_listener = new TcpListener(IPAddress.Loopback, port);
			_listener.Start();
			new Thread(Accept) { IsBackground = true, Name = "Y51 local players" }.Start();
			_log.Info($"local players: other game instances on this PC join through 127.0.0.1:{port} (TCP)");
		}
		catch (Exception ex)
		{
			// Only same-PC test play depends on it; a busy port must not stop the server.
			_listener = null;
			_log.Warn($"local players: no loopback listener on 127.0.0.1:{port} ({ex.GetType().Name}: {ex.Message}); other instances of this Steam account cannot join");
		}
	}

	public event Action<GameServiceUser> ConnectionRequested
	{
		add { _inner.ConnectionRequested += value; _requested += value; }
		remove { _inner.ConnectionRequested -= value; _requested -= value; }
	}

	public event Action<GameServiceUser, ConnectionFailureReason> PeerConnectionFailed
	{
		add { _inner.PeerConnectionFailed += value; _failed += value; }
		remove { _inner.PeerConnectionFailed -= value; _failed -= value; }
	}

	private void Accept()
	{
		while (!_disposed)
		{
			TcpClient client;
			try { client = _listener!.AcceptTcpClient(); }
			catch (Exception) { return; }
			client.NoDelay = true;
			var stream = client.GetStream();
			// Loopback only, so the account a local game names is taken as given (test play on this PC).
			var account = new byte[8];
			client.ReceiveTimeout = 5000;
			try { if (stream.ReadAtLeast(account, 8, throwOnEndOfStream: false) < 8) { client.Close(); continue; } }
			catch (Exception) { client.Close(); continue; }
			client.ReceiveTimeout = 0;
			ulong steamId = BitConverter.ToUInt64(account);
			if (steamId == KeenMods.LocalQuery)
			{
				// A game on this PC asking which mods to load before it joins.
				try
				{
					byte[] text = System.Text.Encoding.UTF8.GetBytes(KeenMods.Current);
					stream.Write(BitConverter.GetBytes(text.Length));
					stream.Write(text);
				}
				catch (Exception) { }
				client.Close();
				continue;
			}
			GameServiceUser user = steamId != 0 ? KeenNet.UserOf(steamId) : default;
			string who;
			// A co-op host already plays as its own account, so another instance of that account is an extra player.
			bool hostAccount = !Y51_SE2Online.Dedicated.DedicatedRuntime.Requested && steamId == Steamworks.SteamUser.GetSteamID().m_SteamID;
			if (steamId != 0 && !hostAccount && !_peers.ContainsKey(user) && !KeenNet.IsSteamPeer(user)) who = $"Steam account {steamId}";
			else
			{
				int slot = 1;
				while (slot <= PeerAlias.MaxSlots && _peers.ContainsKey(LocalPeers.UserFor(slot))) slot++;
				if (slot > PeerAlias.MaxSlots) { client.Close(); continue; }
				user = LocalPeers.UserFor(slot);
				LocalPeers.SetAlias(user, PeerAlias.Make(steamId, slot));
				who = $"extra instance #{slot} of Steam account {steamId}";
			}
			// The game on the other end claims its player by game user; an extra instance must claim this one, not its account's.
			try { stream.Write(user.Id.ToByteArray()); }
			catch (Exception) { client.Close(); continue; }
			_peers[user] = stream;
			_log.Info($"local players: {who} connected over loopback");
			_changes.Enqueue((user, true));
			new Thread(() => Read(user, client, stream)) { IsBackground = true, Name = "Y51 local player " + who }.Start();
		}
	}

	private void Read(GameServiceUser user, TcpClient client, NetworkStream stream)
	{
		try
		{
			while (!_disposed && LocalPeers.ReadFrame(stream, out byte channel, out var reliability, out byte[] data)) Enqueue(user, channel, reliability, data);
		}
		catch (Exception) { }
		finally
		{
			client.Close();
			if (_peers.TryRemove(user, out _)) _changes.Enqueue((user, false));
			LocalPeers.ClearAlias(user);
		}
	}

	/// <summary>Keen's network job thread: raises the connection events where the game expects them.</summary>
	public void Pump()
	{
		RaiseChanges();
		_direct?.Pump((user, connected) =>
		{
			if (connected) _requested?.Invoke(user);
			else _failed?.Invoke(user, ConnectionFailureReason.ClosedByPeer);
		}, Enqueue);
	}

	public bool HasPeer(GameServiceUser user) => _peers.ContainsKey(user);

	public bool AcceptConnection(GameServiceUser user) => _peers.ContainsKey(user) || _direct?.Has(user) == true || _inner.AcceptConnection(user);

	public void CloseConnection(GameServiceUser user)
	{
		if (_peers.TryRemove(user, out var stream)) stream.Close();
		else if (_direct?.Has(user) == true) _direct.Close(user);
		else _inner.CloseConnection(user);
	}

	public bool SendMessage(GameServiceUser user, ReadOnlySpan<byte> data, byte channel = 0, MessageReliability reliability = MessageReliability.Reliable)
	{
		if (!_peers.TryGetValue(user, out var stream))
		{
			bool sent = _direct?.Has(user) == true ? _direct.Send(user, data, channel, reliability) : _inner.SendMessage(user, data, channel, reliability);
			if (!sent && data.Length > 256 * 1024) _log.Warn($"keen send: a {data.Length / 1024} KiB message on channel {channel} to {user} was refused");
			return sent;
		}
		try { LocalPeers.WriteFrame(stream, data, channel, reliability); return true; }
		catch (Exception) { return false; }
	}

	// A local player's connection is queued before its reader thread starts, so raising the queued connections first means
	// Keen never sees a hello from a peer it has not been told about ("Ignoring hello from unaccepted peer", then a 30 s
	// hello timeout closed the player).
	private void RaiseChanges()
	{
		while (_changes.TryDequeue(out var change))
		{
			if (change.Connected) _requested?.Invoke(change.User);
			else _failed?.Invoke(change.User, ConnectionFailureReason.ClosedByPeer);
		}
	}

	public bool TryReadNextMessage(byte channel, out IPeerToPeerSession.Message message)
	{
		if (_inner.TryReadNextMessage(channel, out message)) return true;
		// Only on the game's own channels: those are read on its network job, where its connection events belong
		// (SE2 Online's channels are read on SE2 Online's thread).
		if (channel < Y51_SE2Online.Net.KeenLink.FirstChannel) RaiseChanges();
		return TryDequeue(this, channel, out message);
	}

	void IPeerToPeerSession.DisposeMessage(nint messageHandle) => Free(messageHandle);

	protected override IPeerToPeerSession.Message Create(GameServiceUser from, nint buffer, int length, MessageReliability reliability, IPeerToPeerSession session)
		=> IPeerToPeerSession.CreateMessage(from, buffer, length, buffer, reliability, session);

	public void Dispose()
	{
		_disposed = true;
		try { _listener?.Stop(); } catch (Exception) { }
		_direct?.Dispose();
		foreach (var stream in _peers.Values) stream.Close();
		_peers.Clear();
	}
}

/// <summary>Client side: the connection to a game on this PC, as Keen's game session.</summary>
internal sealed class LocalClientSession : LocalMessageSource, IGameSession, IPeerToPeerSession, IDisposable
{
	private readonly TcpClient _client;

	private readonly NetworkStream _stream;

	private volatile bool _closed;

	private volatile bool _lost;

	public IPeerToPeerSession Communication => this;

	public GameServiceUser Server => LocalPeers.Server;

	public bool IsDedicated => false;

	/// <summary>The game user the server registered this game as (an extra instance's own, not its Steam account's).</summary>
	public GameServiceUser AssignedUser { get; }

	/// <summary>The loopback session this game joined through, if any.</summary>
	public static LocalClientSession? Current { get; private set; }

	public SessionJoinCode? JoinCode => null;

	public LocalClientSession(IPEndPoint endpoint)
	{
		_client = new TcpClient { NoDelay = true };
		_client.Connect(endpoint);
		_stream = _client.GetStream();
		_stream.Write(BitConverter.GetBytes(Steamworks.SteamUser.GetSteamID().m_SteamID));
		var assigned = new byte[16];
		_client.ReceiveTimeout = 10000;
		_stream.ReadExactly(assigned);
		_client.ReceiveTimeout = 0;
		AssignedUser = new GameServiceUser(new Guid(assigned));
		Current = this;
		new Thread(Read) { IsBackground = true, Name = "Y51 local session" }.Start();
	}

	public event Action<GameServiceUser>? ConnectionRequested;

	public event Action<GameServiceUser, ConnectionFailureReason>? PeerConnectionFailed;

	private void Read()
	{
		try
		{
			while (!_closed && LocalPeers.ReadFrame(_stream, out byte channel, out var reliability, out byte[] data)) Enqueue(LocalPeers.Server, channel, reliability, data);
		}
		catch (Exception) { }
		_lost = true;
	}

	/// <summary>Keen's network job thread: reports a lost connection where the game expects it.</summary>
	public void Pump()
	{
		if (_lost && !_closed)
		{
			_closed = true;
			PeerConnectionFailed?.Invoke(LocalPeers.Server, ConnectionFailureReason.ClosedByPeer);
		}
	}

	public bool AcceptConnection(GameServiceUser user) => user == LocalPeers.Server;

	public void CloseConnection(GameServiceUser user) => Dispose();

	public bool SendMessage(GameServiceUser user, ReadOnlySpan<byte> data, byte channel = 0, MessageReliability reliability = MessageReliability.Reliable)
	{
		if (_closed || user != LocalPeers.Server) return false;
		try { LocalPeers.WriteFrame(_stream, data, channel, reliability); return true; }
		catch (Exception) { return false; }
	}

	public bool TryReadNextMessage(byte channel, out IPeerToPeerSession.Message message)
	{
		if (!TryDequeue(this, channel, out message)) return false;
		if (channel < Y51_SE2Online.Net.KeenLink.FirstChannel) JoinBytes.Add(message.GetContents().Length);
		return true;
	}

	void IPeerToPeerSession.DisposeMessage(nint messageHandle) => Free(messageHandle);

	protected override IPeerToPeerSession.Message Create(GameServiceUser from, nint buffer, int length, MessageReliability reliability, IPeerToPeerSession session)
		=> IPeerToPeerSession.CreateMessage(from, buffer, length, buffer, reliability, session);

	public void Dispose()
	{
		if (ReferenceEquals(Current, this)) Current = null;
		_closed = true;
		try { _client.Close(); } catch (Exception) { }
	}
}
