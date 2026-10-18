using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.Net;

public sealed class TcpLink : IPacketLink, IDisposable
{
	private sealed class Conn
	{
		public uint Id;

		public Socket Socket;

		public string Endpoint;

		public bool Outgoing;

		public bool Connected;

		public long StartedMs;

		public readonly Queue<byte[]>[] Out = { new(), new(), new() };

		public byte[]? Current;

		public int CurrentOffset;

		public int PendingBytes;

		public byte[] Recv = new byte[65536];

		public int RecvLen;

		public int RemoteCloseCode;

		public string? RemoteCloseReason;

		public bool Lingering;

		public long LingerDeadlineMs;
	}

	public const ulong SyntheticIdBase = 0x7E00000000000000uL;

	private const byte CloseLane = byte.MaxValue;

	private const int HeaderBytes = 5;

	private readonly IMpLog _log;

	private readonly Func<long> _clock;

	private readonly Dictionary<uint, Conn> _conns = new();

	private readonly List<Conn> _lingering = new();

	private readonly List<LinkEvent> _pendingEvents = new();

	private Socket? _listener;

	private uint _nextId = 1u;

	private bool _disposed;

	public string Name { get; }

	public int MaxMessageSize { get; set; } = 524288;

	public bool AuthenticatesPeers => false;

	public int ConnectTimeoutMs { get; set; } = 10000;

	public int LingerTimeoutMs { get; set; } = 2000;

	public IPEndPoint? ListenEndpoint => _listener?.LocalEndPoint as IPEndPoint;

	public TcpLink(string name, IMpLog log, Func<long>? clock = null)
	{
		Name = name;
		_log = log;
		_clock = clock ?? new Func<long>(MpClock.NowMs);
	}

	public static ulong SyntheticId(uint conn) => SyntheticIdBase | conn;

	public static bool IsSyntheticId(ulong id) => (id & 0xFF00000000000000uL) == SyntheticIdBase;

	public static IPEndPoint ParseEndpoint(string text, IPAddress defaultAddress)
	{
		text = text.Trim();
		if (int.TryParse(text, out var port))
		{
			return new IPEndPoint(defaultAddress, CheckPort(port));
		}
		if (IPEndPoint.TryParse(text, out var parsed) && parsed.Port != 0)
		{
			return new IPEndPoint(parsed.Address, CheckPort(parsed.Port));
		}
		int colon = text.LastIndexOf(':');
		if (colon > 0 && int.TryParse(text.AsSpan(colon + 1), out var hostPort))
		{
			string host = text.Substring(0, colon);
			if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
			{
				return new IPEndPoint(IPAddress.Loopback, CheckPort(hostPort));
			}
			if (IPAddress.TryParse(host, out var address))
			{
				return new IPEndPoint(address, CheckPort(hostPort));
			}
		}
		throw new FormatException($"'{text}' is not <port>, <ipv4>:<port> or localhost:<port>");
	}

	private static int CheckPort(int p)
	{
		if (p <= 0 || p > 65535)
		{
			throw new FormatException($"port {p} out of range");
		}
		return p;
	}

	public void Listen(IPEndPoint endpoint)
	{
		var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
		try
		{
			socket.ExclusiveAddressUse = true;
		}
		catch (Exception)
		{
		}
		socket.Bind(endpoint);
		socket.Listen(128);
		socket.Blocking = false;
		_listener = socket;
		_log.Info($"tcp: listening on {socket.LocalEndPoint}");
	}

	public uint Connect(IPEndPoint endpoint)
	{
		var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
		{
			NoDelay = true,
			Blocking = false
		};
		var conn = new Conn
		{
			Id = _nextId++,
			Socket = socket,
			Endpoint = endpoint.ToString(),
			Outgoing = true,
			StartedMs = _clock()
		};
		try
		{
			socket.Connect(endpoint);
			conn.Connected = true;
		}
		catch (SocketException ex) when ((uint)(ex.SocketErrorCode - SocketError.WouldBlock) <= 2u)
		{
			// WouldBlock, InProgress or AlreadyInProgress: the non-blocking connect is still in flight.
		}
		catch (SocketException ex)
		{
			socket.Dispose();
			_pendingEvents.Add(new LinkEvent(LinkEventKind.Disconnected, conn.Id, SyntheticId(conn.Id), (int)ex.SocketErrorCode, $"connect to {endpoint} failed: {ex.SocketErrorCode}"));
			return conn.Id;
		}
		_conns[conn.Id] = conn;
		_log.Info($"tcp: connecting to {endpoint} (conn {conn.Id})");
		if (conn.Connected)
		{
			OnConnected(conn, _pendingEvents);
		}
		return conn.Id;
	}

	public bool HasConnection(uint conn) => _conns.ContainsKey(conn);

	public void Pump(List<LinkEvent> events, List<LinkMessage> messages)
	{
		if (_disposed)
		{
			return;
		}
		if (_pendingEvents.Count > 0)
		{
			events.AddRange(_pendingEvents);
			_pendingEvents.Clear();
		}
		Accept(events);
		long now = _clock();
		foreach (var c in Snapshot())
		{
			if (!c.Connected)
			{
				PollConnect(c, events, now);
			}
			else if (Flush(c, events))
			{
				Receive(c, events, messages);
			}
		}
		ServiceLingering(now);
	}

	private List<Conn> Snapshot() => new(_conns.Values);

	private void Accept(List<LinkEvent> events)
	{
		if (_listener is null)
		{
			return;
		}
		for (int i = 0; i < 16; i++)
		{
			Socket socket;
			try
			{
				socket = _listener.Accept();
			}
			catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock)
			{
				break;
			}
			catch (SocketException ex)
			{
				_log.WarnOnce("tcp-accept-" + ex.SocketErrorCode, $"tcp: accept failed: {ex.SocketErrorCode}");
				break;
			}
			socket.Blocking = false;
			socket.NoDelay = true;
			var conn = new Conn
			{
				Id = _nextId++,
				Socket = socket,
				Endpoint = socket.RemoteEndPoint?.ToString() ?? "?",
				StartedMs = _clock()
			};
			_conns[conn.Id] = conn;
			_log.Info($"tcp: accepted connection from {conn.Endpoint} (conn {conn.Id})");
			OnConnected(conn, events);
		}
	}

	private void PollConnect(Conn c, List<LinkEvent> events, long now)
	{
		try
		{
			if (c.Socket.Poll(0, SelectMode.SelectWrite))
			{
				c.Connected = true;
				OnConnected(c, events);
				return;
			}
			if (c.Socket.Poll(0, SelectMode.SelectError))
			{
				var error = (SocketError)(int)c.Socket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Error);
				Fail(c, events, (int)error, $"connect to {c.Endpoint} failed: {error}");
				return;
			}
		}
		catch (SocketException ex)
		{
			Fail(c, events, (int)ex.SocketErrorCode, $"connect to {c.Endpoint} failed: {ex.SocketErrorCode}");
			return;
		}
		if (now - c.StartedMs > ConnectTimeoutMs)
		{
			Fail(c, events, 10060, $"connect to {c.Endpoint} timed out after {ConnectTimeoutMs} ms");
		}
	}

	private void OnConnected(Conn c, List<LinkEvent> events)
	{
		_log.Info($"tcp: conn {c.Id} {(c.Outgoing ? "to" : "from")} {c.Endpoint}: CONNECTED (lanes: per-connection priority queues)");
		events.Add(new LinkEvent(LinkEventKind.Connected, c.Id, SyntheticId(c.Id)));
	}

	private void Fail(Conn c, List<LinkEvent> events, int code, string reason)
	{
		if (_conns.Remove(c.Id))
		{
			Dispose(c.Socket);
			events.Add(new LinkEvent(LinkEventKind.Disconnected, c.Id, SyntheticId(c.Id), code, reason));
		}
	}

	private bool Flush(Conn c, List<LinkEvent>? events)
	{
		while (true)
		{
			if (c.Current is null)
			{
				c.Current = NextMessage(c);
				c.CurrentOffset = 0;
				if (c.Current is null)
				{
					return true;
				}
			}
			int sent;
			try
			{
				sent = c.Socket.Send(c.Current, c.CurrentOffset, c.Current.Length - c.CurrentOffset, SocketFlags.None);
			}
			catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock)
			{
				return true;
			}
			catch (SocketException ex)
			{
				if (events is not null)
				{
					Fail(c, events, (int)ex.SocketErrorCode, $"send failed: {ex.SocketErrorCode}");
				}
				return false;
			}
			catch (ObjectDisposedException)
			{
				return false;
			}
			c.CurrentOffset += sent;
			c.PendingBytes -= sent;
			if (c.CurrentOffset < c.Current.Length)
			{
				break;
			}
			c.Current = null;
		}
		return true;
	}

	private static byte[]? NextMessage(Conn c)
	{
		foreach (var channel in LanePlan.ServiceOrder)
		{
			var queue = c.Out[(uint)channel];
			if (queue.Count > 0)
			{
				return queue.Dequeue();
			}
		}
		return null;
	}

	private void Receive(Conn c, List<LinkEvent> events, List<LinkMessage> messages)
	{
		for (int i = 0; i < 64; i++)
		{
			if (c.RecvLen == c.Recv.Length)
			{
				Array.Resize(ref c.Recv, c.Recv.Length * 2);
			}
			int received;
			try
			{
				received = c.Socket.Receive(c.Recv, c.RecvLen, c.Recv.Length - c.RecvLen, SocketFlags.None);
			}
			catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock)
			{
				break;
			}
			catch (SocketException ex)
			{
				bool byPeer = (uint)(ex.SocketErrorCode - SocketError.ConnectionAborted) <= 1u; // aborted or reset
				Closed(c, events, byPeer, (int)ex.SocketErrorCode, $"receive failed: {ex.SocketErrorCode}");
				break;
			}
			if (received == 0)
			{
				Parse(c, messages);
				Closed(c, events, byPeer: true, c.RemoteCloseCode, c.RemoteCloseReason ?? "connection closed by peer");
				break;
			}
			c.RecvLen += received;
			if (!Parse(c, messages))
			{
				Closed(c, events, byPeer: false, DisconnectCodes.ProtocolError, "bad tcp framing");
				break;
			}
		}
	}

	// Frame: [u32 length of lane + payload][u8 lane][payload]
	private bool Parse(Conn c, List<LinkMessage> messages)
	{
		int offset;
		int length;
		for (offset = 0; c.RecvLen - offset >= 4; offset += 4 + length)
		{
			length = (int)BinaryPrimitives.ReadUInt32LittleEndian(c.Recv.AsSpan(offset));
			if (length < 1 || length > MaxMessageSize + 1)
			{
				return false;
			}
			if (c.RecvLen - offset - 4 < length)
			{
				break;
			}
			byte lane = c.Recv[offset + 4];
			var payload = c.Recv.AsSpan(offset + HeaderBytes, length - 1);
			if (lane == CloseLane)
			{
				// Close payload: [i32 code][u16 reason length][utf8 reason]
				if (payload.Length >= 6)
				{
					c.RemoteCloseCode = BinaryPrimitives.ReadInt32LittleEndian(payload);
					int reasonLength = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(4));
					c.RemoteCloseReason = Encoding.UTF8.GetString(payload.Slice(6, Math.Min(reasonLength, payload.Length - 6)));
				}
			}
			else
			{
				if (lane > (byte)Channel.State)
				{
					return false;
				}
				messages.Add(new LinkMessage(c.Id, payload.ToArray()));
			}
		}
		if (offset > 0)
		{
			Buffer.BlockCopy(c.Recv, offset, c.Recv, 0, c.RecvLen - offset);
			c.RecvLen -= offset;
		}
		return true;
	}

	private void Closed(Conn c, List<LinkEvent> events, bool byPeer, int code, string reason)
	{
		if (_conns.Remove(c.Id))
		{
			Dispose(c.Socket);
			events.Add(new LinkEvent(LinkEventKind.Disconnected, c.Id, SyntheticId(c.Id), code, reason, byPeer));
		}
	}

	public LinkSendResult Send(uint conn, Channel channel, bool reliable, ReadOnlySpan<byte> frame)
	{
		if (!_conns.TryGetValue(conn, out var c) || !c.Connected)
		{
			return LinkSendResult.Failed;
		}
		if (frame.Length > MaxMessageSize)
		{
			return LinkSendResult.Failed;
		}
		byte[] message = new byte[HeaderBytes + frame.Length];
		BinaryPrimitives.WriteUInt32LittleEndian(message, (uint)(frame.Length + 1));
		message[4] = (byte)channel;
		frame.CopyTo(message.AsSpan(HeaderBytes));
		c.Out[(uint)channel].Enqueue(message);
		c.PendingBytes += message.Length;
		Flush(c, _pendingEvents);
		return LinkSendResult.Ok;
	}

	public int GetPendingReliableBytes(uint conn)
	{
		if (!_conns.TryGetValue(conn, out var c))
		{
			return int.MaxValue;
		}
		return c.PendingBytes;
	}

	public void Close(uint conn, int code, string reason, bool linger)
	{
		if (!_conns.Remove(conn, out var c))
		{
			return;
		}
		byte[] reasonBytes = Encoding.UTF8.GetBytes(reason.Length > 200 ? reason.Substring(0, 200) : reason);
		byte[] message = new byte[11 + reasonBytes.Length];
		BinaryPrimitives.WriteUInt32LittleEndian(message, (uint)(7 + reasonBytes.Length));
		message[4] = CloseLane;
		BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(5), code);
		BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(9), (ushort)reasonBytes.Length);
		reasonBytes.CopyTo(message.AsSpan(11));
		if (!c.Connected)
		{
			Dispose(c.Socket);
			return;
		}
		if (!linger)
		{
			for (int i = 0; i < c.Out.Length; i++)
			{
				c.Out[i].Clear();
			}
			// A message already partly on the wire has to finish, or the peer loses framing.
			if (c.Current is not null && c.CurrentOffset == 0)
			{
				c.Current = null;
			}
		}
		c.Out[(int)Channel.Bulk].Enqueue(message);
		c.PendingBytes += message.Length;
		c.Lingering = true;
		c.LingerDeadlineMs = _clock() + (linger ? LingerTimeoutMs : 200);
		_lingering.Add(c);
		ServiceLingering(_clock());
	}

	private void ServiceLingering(long now)
	{
		for (int i = _lingering.Count - 1; i >= 0; i--)
		{
			var c = _lingering[i];
			if (!Flush(c, null) || (c.Current == null && c.Out[0].Count == 0 && c.Out[1].Count == 0 && c.Out[2].Count == 0) || now > c.LingerDeadlineMs)
			{
				try
				{
					c.Socket.Shutdown(SocketShutdown.Both);
				}
				catch (Exception)
				{
				}
				Dispose(c.Socket);
				_lingering.RemoveAt(i);
			}
		}
	}

	public IEnumerable<(uint Conn, string Endpoint, bool Connected)> Connections()
	{
		foreach (var c in _conns.Values)
		{
			yield return (c.Id, c.Endpoint, c.Connected);
		}
	}

	public string QuickStatus(uint conn)
	{
		if (!_conns.TryGetValue(conn, out var c))
		{
			return "no such connection";
		}
		return $"route=TCP {c.Endpoint} pending={c.PendingBytes} B queued ctl/state/bulk={c.Out[0].Count}/{c.Out[2].Count}/{c.Out[1].Count}";
	}

	private static void Dispose(Socket s)
	{
		try
		{
			s.Dispose();
		}
		catch (Exception)
		{
		}
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		foreach (uint conn in new List<uint>(_conns.Keys))
		{
			Close(conn, DisconnectCodes.Shutdown, "shutting down", linger: true);
		}
		long deadline = _clock() + 500;
		while (_lingering.Count > 0 && _clock() < deadline)
		{
			ServiceLingering(_clock());
			Thread.Sleep(1);
		}
		ServiceLingering(long.MaxValue);
		_disposed = true;
		if (_listener is not null)
		{
			Dispose(_listener);
		}
	}
}
