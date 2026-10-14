using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading;
using HarmonyLib;
using Keen.VRage.DCS.Components;
using Keen.VRage.DCS.Utils;
using Keen.VRage.Library.Reflection;
using Keen.VRage.Multiplayer.Replications;
using Keen.VRage.Multiplayer.Sessions;
using Y51_SE2Online.Core;
using Y51_SE2Online.SyncEngine;

namespace Y51_SE2Online.Probe;

internal static class ReplicationProbe
{
	private enum StubKind : byte
	{
		Signal,
		PostInvoke,
		PostData,
		RemoveData
	}

	private const string PostInvokeSignal = "Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent+PostInvokeInternalSignal";

	private const string PostDataSignal = "Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent+PostDataSignal";

	private const string RemoveDataSignal = "Keen.VRage.Multiplayer.Replications.InProcess.InProcessReplicationSessionComponent+RemoveDataSignal";

	private static IMpLog _log = GameLog.Instance;

	private static readonly object Sync = new();

	private static volatile bool _commitActive;

	private static volatile bool _measure;

	private static volatile IS2CTap? _tap;

	private static bool _meterAllowed;

	private static Func<object, bool>? _isServer;

	private static Func<object, object>? _replayContext;

	private static FieldInfo? _replayTableField;

	private static object? _serverComp;

	private static object? _clientComp;

	private static WeakReference<object>? _serverCtx;

	private static WeakReference<object>? _clientCtx;

	private static nint[]? _origReaders;

	private static StubKind[] _stubs = Array.Empty<StubKind>();

	private static readonly ConditionalWeakTable<ReplicationCommandBuffer.Stub[], object> Instrumented = new();

	public static IS2CTap? Tap
	{
		get => _tap;
		set => _tap = value;
	}

	public static bool MeterArmed => _meterAllowed && _commitActive;

	public unsafe static bool IsGuarded(object component)
	{
		try
		{
			if (!MeterArmed || _replayTableField == null)
			{
				return false;
			}
			ReplicationCommandBuffer.Stub[] stubs = ((ReplicationCommandBuffer.ReplayTable?)_replayTableField.GetValue(component))?.Stubs;
			if (stubs == null || stubs.Length == 0)
			{
				return false;
			}
			delegate*<object, ref CommandBufferState, nint, ReplicationCommandBuffer, void> meter = &MeasuringReader;
			return stubs[0].Wrapper.Reader == meter;
		}
		catch (Exception)
		{
			return false;
		}
	}

	public static List<(string Hook, bool Ok, string Detail)> Install(Harmony harmony, IMpLog log)
	{
		_log = log;
		ReplayGuard.Log = log;
		var results = new List<(string, bool, string)>();
		results.Add(TryInstall("probe:CommitFrame", () =>
		{
			MethodInfo original = EngineMembers.Method(HookTargets.CommitFrame);
			_isServer = EngineMembers.Getter<bool>(EngineMembers.Property(R("ipr-isserver")));
			_replayContext = EngineMembers.Getter<object>(EngineMembers.Property(R("ipr-replayctx")));
			_replayTableField = EngineMembers.Field(R("ipr-replaytable"));
			// The command meter parses Replay's command header, so it only runs against the exact IL it was written for.
			byte[] replayIl = EngineMembers.Method(HookTargets.Replay).GetMethodBody()?.GetILAsByteArray() ?? Array.Empty<byte>();
			string hash = Convert.ToHexString(SHA256.HashData(replayIl)).ToLowerInvariant();
			// A tool that rewrites the game's assemblies (native Linux setups) renumbers metadata tokens without changing the code.
			string shape = IlShape.Hash(replayIl);
			_meterAllowed = hash == HookTargets.ReplayIlSha256 || shape == HookTargets.ReplayIlShapeSha256;
			harmony.Patch(original, null, new HarmonyMethod(typeof(ReplicationProbe), "CommitPostfix"));
			_commitActive = true;
			_measure = _meterAllowed;
			return _meterAllowed
				? $"ok; command meter armed (Replay IL {replayIl.Length} B, sha256 matches build {HookTargets.GameBuild})"
				: $"ok, but Replay IL ({replayIl.Length} B) sha256 {hash}, shape {shape} != expected (game updated?): command meter OFF";
		}));
		return results;

		static EngineMember R(string id) => HookTargets.Reflected.Single(m => m.Id == id);
	}

	private static (string, bool, string) TryInstall(string name, Func<string> install)
	{
		try
		{
			return (name, true, install());
		}
		catch (Exception ex)
		{
			return (name, false, ex is MissingMemberException ? ex.Message : ex.ToString());
		}
	}

	private static void Disable(string where, Exception e)
	{
		_commitActive = _measure = false;
		_log.Error($"probe: {where} failed, probe disabled (game unaffected): {e}");
	}

	private unsafe static void InstrumentTable(ReplicationCommandBuffer.Stub[] stubs)
	{
		if (!_meterAllowed || stubs.Length == 0 || Instrumented.TryGetValue(stubs, out _))
		{
			return;
		}
		lock (Sync)
		{
			if (Instrumented.TryGetValue(stubs, out _))
			{
				return;
			}
			nint[] readers = new nint[stubs.Length];
			for (int i = 0; i < stubs.Length; i++)
			{
				readers[i] = (nint)stubs[i].Wrapper.Reader;
			}
			delegate*<object, ref CommandBufferState, nint, ReplicationCommandBuffer, void> meter = &MeasuringReader;
			if (_origReaders != null)
			{
				if (_origReaders.Length != readers.Length || !_origReaders.AsSpan().SequenceEqual(readers))
				{
					if (!readers.All(r => r == (nint)meter))
					{
						_log.Warn($"probe: another replay table with different readers ({readers.Length} entries); not metering it");
					}
					Instrumented.AddOrUpdate(stubs, new object());
					return;
				}
			}
			else
			{
				_stubs = BuildStubKinds(readers);
				Volatile.Write(ref _origReaders, readers);
			}
			for (int i = 0; i < stubs.Length; i++)
			{
				stubs[i].Wrapper = new ReaderSignature(meter);
			}
			Instrumented.AddOrUpdate(stubs, new object());
			_log.Info($"probe: command meter live on {stubs.Length} replicated command types ({_stubs.Count(s => s != StubKind.Signal)} generic InProcess commands)");
		}
	}

	// The replay tables are shared with single player, so the game's own readers go back when the session ends.
	private unsafe static void RestoreReaders()
	{
		lock (Sync)
		{
			if (_origReaders is not { } originals)
			{
				return;
			}
			delegate*<object, ref CommandBufferState, nint, ReplicationCommandBuffer, void> meter = &MeasuringReader;
			foreach (var (stubs, _) in Instrumented)
			{
				for (int i = 0; i < stubs.Length && i < originals.Length; i++)
				{
					if ((nint)stubs[i].Wrapper.Reader == (nint)meter)
					{
						stubs[i].Wrapper = new ReaderSignature((delegate*<object, ref CommandBufferState, nint, ReplicationCommandBuffer, void>)originals[i]);
					}
				}
			}
			Instrumented.Clear();
		}
	}

	private unsafe static void MeasuringReader(object context, ref CommandBufferState st, nint callback, ReplicationCommandBuffer cb)
	{
		if (!SlowTicks.Enabled)
		{
			MeasuringReaderCore(context, ref st, callback, cb);
			return;
		}
		int stub = Unsafe.ReadUnaligned<int>(st.DataPtr + st.DataOffset - 4);
		long start = Stopwatch.GetTimestamp();
		MeasuringReaderCore(context, ref st, callback, cb);
		SlowTicks.Record(stub, Stopwatch.GetTimestamp() - start);
	}

	private unsafe static void MeasuringReaderCore(object context, ref CommandBufferState st, nint callback, ReplicationCommandBuffer cb)
	{
		byte* dataPtr = st.DataPtr;
		nint dataOffset = st.DataOffset;
		int stub = Unsafe.ReadUnaligned<int>(dataPtr + dataOffset - 4);
		var original = (delegate*<object, ref CommandBufferState, nint, ReplicationCommandBuffer, void>)_origReaders[stub];
		if (dataOffset >= 8 && Unsafe.ReadUnaligned<int>(dataPtr + dataOffset - 8) == 1580056798)
		{
			if (AudioGate.Enabled && AudioGate.Mute(stub, cb, dataPtr + dataOffset, st.ActiveBytes - dataOffset, injected: true, s2c: true))
			{
				AudioGate.Enter();
				try
				{
					ReplayGuard.Invoke(original, context, ref st, callback, cb, stub);
					return;
				}
				finally
				{
					AudioGate.Exit();
				}
			}
			ReplayGuard.Invoke(original, context, ref st, callback, cb, stub);
			return;
		}
		if ((ControlGuard.Enabled || ControlGuard.HostFlightGate) && _serverCtx != null && _serverCtx.TryGetTarget(out object server) && server == context)
		{
			int skip = -1;
			try
			{
				StubKind[] stubs = _stubs;
				bool flightCommand = ControlGuard.HostFlightGate && stub >= 0 && stub < stubs.Length;
				if (flightCommand)
				{
					flightCommand = stubs[stub] is StubKind.PostData or StubKind.RemoveData;
				}
				if (flightCommand)
				{
					skip = ControlGuard.HostFlightSkip(stubs[stub] == StubKind.RemoveData, dataPtr + dataOffset, st.ActiveBytes - dataOffset, cb);
				}
				else if (ControlGuard.Enabled)
				{
					skip = ControlGuard.HostClientSkip(stub, dataPtr + dataOffset, st.ActiveBytes - dataOffset, cb);
				}
			}
			catch (Exception ex)
			{
				ControlGuard.Enabled = false;
				ControlGuard.HostFlightGate = false;
				_log.Error($"control guard failed and is switched off (the game is unaffected): {ex}");
			}
			if (skip >= 0)
			{
				st.DataOffset = dataOffset + skip;
				return;
			}
		}
		if (!_measure)
		{
			original(context, ref st, callback, cb);
			return;
		}
		bool s2c = false;
		IS2CTap tap = _tap;
		try
		{
			s2c = IsClientSide(context);
			if (tap != null && s2c)
			{
				Classify(cb, stub, dataPtr + dataOffset, st.ActiveBytes - dataOffset, out StubKind kind, out object? key, out int intKey, out int payloadBytes);
				if (kind != StubKind.Signal)
				{
					TapCommand(tap, cb, kind, dataPtr + dataOffset, st.ActiveBytes - dataOffset, key, intKey, payloadBytes);
				}
				else
				{
					tap.OnSignal(cb, stub, new ReadOnlySpan<byte>(dataPtr + dataOffset, (int)Math.Min(int.MaxValue, st.ActiveBytes - dataOffset)));
				}
			}
		}
		catch (Exception ex)
		{
			if (_tap == tap)
			{
				_tap = null;
			}
			_log.Error($"host tap failed, world sync stops forwarding state (game unaffected): {ex}");
		}
		if (tap != null && AudioGate.Enabled && s2c && AudioGate.Mute(stub, cb, dataPtr + dataOffset, st.ActiveBytes - dataOffset, injected: false, s2c: true))
		{
			AudioGate.Enter();
			try
			{
				original(context, ref st, callback, cb);
			}
			finally
			{
				AudioGate.Exit();
			}
		}
		else
		{
			original(context, ref st, callback, cb);
		}
	}

	private unsafe static void TapCommand(IS2CTap tap, ReplicationCommandBuffer cb, StubKind kind, byte* payload, nint available, object? key, int typeId, int length)
	{
		switch (kind)
		{
		case StubKind.PostData:
		{
			int sourceRef = ReadInt(payload, available, 4);
			if (typeId >= 0 && length >= 0 && (nint)16 + (nint)length <= available)
			{
				Entity? source = sourceRef >= 0 ? cb.ReadObject(sourceRef) as Entity : null;
				tap.OnPostData(source, typeId, new ReadOnlySpan<byte>(payload + 16, length), cb);
			}
			break;
		}
		case StubKind.RemoveData:
		{
			int sourceRef = ReadInt(payload, available, 4);
			if (typeId >= 0)
			{
				tap.OnRemoveData(sourceRef >= 0 ? cb.ReadObject(sourceRef) as Entity : null, typeId);
			}
			break;
		}
		case StubKind.PostInvoke:
			if (key is Delegate callback)
			{
				tap.OnInvoke(callback);
			}
			break;
		}
	}

	// The server side is checked first: a context registered as the server is never treated as S2C.
	private static bool IsClientSide(object context)
	{
		if (_serverCtx != null && _serverCtx.TryGetTarget(out object server) && server == context)
		{
			return false;
		}
		return _clientCtx != null && _clientCtx.TryGetTarget(out object client) && client == context;
	}

	private unsafe static StubKind[] BuildStubKinds(nint[] readers)
	{
		ReplicationCommandBufferIndexer.CommandBufferCommand[] table;
		try
		{
			table = MetadataIndexerBase<ReplicationCommandBufferIndexer>.Instance.Table;
		}
		catch (Exception ex)
		{
			_log.Warn($"probe: command indexer unavailable ({ex.GetType().Name}: {ex.Message}); commands reported by stub index only");
			table = Array.Empty<ReplicationCommandBufferIndexer.CommandBufferCommand>();
		}
		var kinds = new StubKind[readers.Length];
		bool aligned = table.Length == readers.Length;
		int mismatches = 0;
		for (int i = 0; i < readers.Length; i++)
		{
			if (aligned && table[i].Reader.Reader != (delegate*<object, ref CommandBufferState, IntPtr, ReplicationCommandBuffer, void>)readers[i])
			{
				mismatches++;
			}
			kinds[i] = (aligned ? table[i].SignalType?.FullName : null) switch
			{
				PostInvokeSignal => StubKind.PostInvoke,
				PostDataSignal => StubKind.PostData,
				RemoveDataSignal => StubKind.RemoveData,
				_ => StubKind.Signal,
			};
		}
		if (!aligned || mismatches > 0)
		{
			_log.Warn($"probe: replay table ({readers.Length}) and command indexer ({table.Length}) disagree ({mismatches} reader mismatches); signal names may be wrong");
		}
		return kinds;
	}

	private unsafe static int ReadInt(byte* payload, nint available, int offset)
	{
		if (offset + 4 > available)
		{
			return -1;
		}
		return Unsafe.ReadUnaligned<int>(payload + offset);
	}

	private unsafe static void Classify(ReplicationCommandBuffer cb, int num, byte* payload, nint available, out StubKind kind, out object? key, out int intKey, out int payloadBytes)
	{
		kind = StubKind.Signal;
		key = null;
		intKey = num;
		payloadBytes = 0;
		StubKind[] stubs = _stubs;
		if ((uint)num >= (uint)stubs.Length)
		{
			return;
		}
		kind = stubs[num];
		switch (kind)
		{
		case StubKind.PostInvoke:
		{
			int delegateRef = ReadInt(payload, available, 4);
			key = delegateRef >= 0 ? cb.ReadObject(delegateRef) : null;
			break;
		}
		case StubKind.PostData:
			intKey = ReadInt(payload, available, 8);
			payloadBytes = ReadInt(payload, available, 12);
			break;
		case StubKind.RemoveData:
			intKey = ReadInt(payload, available, 8);
			break;
		}
	}

	private static void CommitPostfix(object __instance)
	{
		if (!_commitActive)
		{
			return;
		}
		try
		{
			if (__instance != _serverComp && __instance != _clientComp)
			{
				Attach(__instance);
			}
		}
		catch (Exception e)
		{
			Disable("CommitFrame postfix", e);
		}
	}

	private static void Attach(object component)
	{
		bool isServer = _isServer(component);
		object replayContext = _replayContext(component);
		if (isServer)
		{
			_serverComp = component;
			_serverCtx = new WeakReference<object>(replayContext);
		}
		else
		{
			_clientComp = component;
			_clientCtx = new WeakReference<object>(replayContext);
		}
		var table = (ReplicationCommandBuffer.ReplayTable)_replayTableField.GetValue(component);
		_log.Info($"tap: {(isServer ? "server" : "client")} scene replication component attached (replay table {table.Stubs?.Length ?? 0} entries)");
		if (table.Stubs != null)
		{
			InstrumentTable(table.Stubs);
		}
	}

	internal static void RegisterSideForTest(object replayContext, bool server)
	{
		if (server)
		{
			_serverCtx = new WeakReference<object>(replayContext);
		}
		else
		{
			_clientCtx = new WeakReference<object>(replayContext);
		}
	}

	internal static void InstrumentForTest(ReplicationCommandBuffer.Stub[] stubs) => InstrumentTable(stubs);

	public static void Stop()
	{
		_tap = null;
		_commitActive = _measure = false;
		RestoreReaders();
		// The world outlives a network restart (e.g. a host settings change): forget its components so the next
		// CommitFrame attaches and instruments them again; otherwise the replay guard stays off for that world.
		_serverComp = _clientComp = null;
	}
}
