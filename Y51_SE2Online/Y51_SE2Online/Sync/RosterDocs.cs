using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Y51_SE2Online.Net;

namespace Y51_SE2Online.Sync;

/// <summary>
/// The top-level properties of one roster-style message (admin state, social snapshot, player table, name tags).
/// Never mutated once built, so every player's document can share the same property values: what changed in a shared
/// value is worked out once per pass (<see cref="DocDiffCache"/>), not once per player.
/// </summary>
public sealed class JsonDoc
{
	internal readonly KeyValuePair<string, JsonNode?>[] Props;

	private byte[]? _json;

	private JsonDoc(KeyValuePair<string, JsonNode?>[] props) => Props = props;

	public static JsonDoc Of<T>(T value) => new(JsonSerializer.SerializeToNode(value)!.AsObject().ToArray());

	public JsonNode? this[string name] => Props.FirstOrDefault(p => p.Key == name).Value;

	public JsonDoc With(string name, JsonNode? value)
	{
		var props = (KeyValuePair<string, JsonNode?>[])Props.Clone();
		int i = Array.FindIndex(props, p => p.Key == name);
		if (i < 0) return new JsonDoc(props.Append(new(name, value)).ToArray());
		props[i] = new(name, value);
		return new JsonDoc(props);
	}

	/// <summary>Takes <paramref name="names"/> from <paramref name="from"/> by reference.</summary>
	public JsonDoc With(JsonDoc from, IEnumerable<string> names)
	{
		var props = (KeyValuePair<string, JsonNode?>[])Props.Clone();
		foreach (string name in names)
		{
			int i = Array.FindIndex(props, p => p.Key == name);
			if (i >= 0) props[i] = new(name, from[name]);
		}
		return new JsonDoc(props);
	}

	/// <summary>The whole document, as its type serializes.</summary>
	public byte[] Json => _json ??= Write(w =>
	{
		w.WriteStartObject();
		foreach (var (name, value) in Props)
		{
			w.WritePropertyName(name);
			if (value == null) w.WriteNullValue(); else value.WriteTo(w);
		}
		w.WriteEndObject();
	});

	/// <summary>The message an older client reads: the type byte, then the JSON (what <see cref="SocialMessage.Encode"/> writes).</summary>
	public byte[] Encode(SyncMsg type)
	{
		byte[] json = Json;
		var data = new byte[json.Length + 1];
		data[0] = (byte)type;
		json.CopyTo(data, 1);
		return data;
	}

	internal static byte[] Write(Action<Utf8JsonWriter> write)
	{
		var stream = new MemoryStream();
		using (var w = new Utf8JsonWriter(stream)) write(w);
		return stream.ToArray();
	}
}

/// <summary>
/// JSON patches. {"=":v} replaces; {"p":{name:patch}} patches some properties of an object; {"n":count,"i":[[index,patch]]}
/// patches an array by position; {"k":key,"d":[keys],"u":[[key,patch]],"r":[keys]} patches an array of objects by their
/// first property (removed, upserted, and the new order when it is not the old one with new entries appended).
/// </summary>
public static class JsonPatch
{
	public const int MaxArray = 65536;

	/// <summary>The patch that turns <paramref name="a"/> into <paramref name="b"/>, or null when they are equal.</summary>
	public static JsonNode? Diff(JsonNode? a, JsonNode? b)
	{
		if (a is JsonObject oa && b is JsonObject ob)
		{
			var props = new JsonObject();
			foreach (var (name, vb) in ob)
			{
				if (!oa.TryGetPropertyValue(name, out var va)) props[name] = Replace(vb);
				else if (Diff(va, vb) is { } patch) props[name] = patch;
			}
			if (oa.Any(p => !ob.ContainsKey(p.Key))) return Replace(b);
			return props.Count == 0 ? null : new JsonObject { ["p"] = props };
		}
		if (a is JsonArray aa && b is JsonArray ab)
		{
			var patch = Keyed(aa, ab, out bool same) ?? (same ? null : Positional(aa, ab));
			if (patch == null) return null;
			var whole = Replace(b);
			return patch.ToJsonString().Length < whole.ToJsonString().Length ? patch : whole;
		}
		return JsonNode.DeepEquals(a, b) ? null : Replace(b);
	}

	private static JsonObject Replace(JsonNode? value) => new() { ["="] = value?.DeepClone() };

	private static JsonObject? Positional(JsonArray a, JsonArray b)
	{
		var items = new JsonArray();
		for (int i = 0; i < b.Count; i++)
		{
			var patch = i < a.Count ? Diff(a[i], b[i]) : Replace(b[i]);
			if (patch != null) items.Add(new JsonArray(i, patch));
		}
		return items.Count == 0 && a.Count == b.Count ? null : new JsonObject { ["n"] = b.Count, ["i"] = items };
	}

	private static string? KeyOf(JsonArray array)
		=> array.Count > 0 && array[0] is JsonObject first && first.Count > 0 ? first.First().Key : null;

	private static List<(string Key, JsonNode Node)>? Keys(JsonArray array, string key)
	{
		var list = new List<(string, JsonNode)>(array.Count);
		var seen = new HashSet<string>();
		foreach (var node in array)
		{
			if (node is not JsonObject o || !o.TryGetPropertyValue(key, out var k) || k is not JsonValue) return null;
			string text = k.ToJsonString();
			if (!seen.Add(text)) return null;
			list.Add((text, node));
		}
		return list;
	}

	// Null when the arrays have no unique first-property keys; same is set when they key alike and nothing changed.
	private static JsonObject? Keyed(JsonArray a, JsonArray b, out bool same)
	{
		same = false;
		string? key = KeyOf(b) ?? KeyOf(a);
		if (key == null) return null;
		var ka = Keys(a, key);
		var kb = Keys(b, key);
		if (ka == null || kb == null) return null;
		var old = ka.ToDictionary(x => x.Key, x => x.Node);
		var removed = new JsonArray();
		var upserts = new JsonArray();
		var inB = kb.Select(x => x.Key).ToHashSet();
		foreach (var (k, node) in ka)
			if (!inB.Contains(k)) removed.Add(node[key]!.DeepClone());
		var order = ka.Where(x => inB.Contains(x.Key)).Select(x => x.Key).ToList();
		foreach (var (k, node) in kb)
		{
			if (old.TryGetValue(k, out var before))
			{
				if (Diff(before, node) is { } patch) upserts.Add(new JsonArray(node[key]!.DeepClone(), patch));
			}
			else
			{
				upserts.Add(new JsonArray(node[key]!.DeepClone(), Replace(node)));
				order.Add(k);
			}
		}
		bool reordered = !order.SequenceEqual(kb.Select(x => x.Key));
		if (removed.Count == 0 && upserts.Count == 0 && !reordered)
		{
			same = true;
			return null;
		}
		var result = new JsonObject { ["k"] = key, ["d"] = removed, ["u"] = upserts };
		if (reordered) result["r"] = new JsonArray(kb.Select(x => x.Node[key]!.DeepClone()).ToArray());
		return result;
	}

	/// <summary>Applies <paramref name="patch"/> to <paramref name="value"/>, in place where it can; returns the new value.</summary>
	public static JsonNode? Apply(JsonNode? value, JsonNode patch)
	{
		var p = patch.AsObject();
		if (p.TryGetPropertyValue("=", out var replace)) return replace?.DeepClone();
		if (p.TryGetPropertyValue("p", out var props))
		{
			var obj = (JsonObject)value!;
			foreach (var (name, change) in props!.AsObject())
			{
				obj.TryGetPropertyValue(name, out var current);
				var next = Apply(current, change!);
				if (!ReferenceEquals(next, current)) obj[name] = next;
			}
			return obj;
		}
		var array = (JsonArray)value!;
		if (p.TryGetPropertyValue("n", out var count))
		{
			int n = count!.GetValue<int>();
			if (n < 0 || n > MaxArray) throw new InvalidDataException("array patch too large");
			while (array.Count > n) array.RemoveAt(array.Count - 1);
			while (array.Count < n) array.Add(null);
			foreach (var item in p["i"]!.AsArray())
			{
				int i = item![0]!.GetValue<int>();
				var current = array[i];
				var next = Apply(current, item[1]!);
				if (!ReferenceEquals(next, current)) array[i] = next;
			}
			return array;
		}
		string key = p["k"]!.GetValue<string>();
		var nodes = array.ToList();
		array.Clear();
		var list = nodes.Select(n => (Key: n![key]!.ToJsonString(), Node: n)).ToList();
		var removed = p["d"]!.AsArray().Select(k => k!.ToJsonString()).ToHashSet();
		list.RemoveAll(x => removed.Contains(x.Key));
		foreach (var item in p["u"]!.AsArray())
		{
			string k = item![0]!.ToJsonString();
			int i = list.FindIndex(x => x.Key == k);
			if (i >= 0) list[i] = (k, Apply(list[i].Node, item[1]!));
			else list.Add((k, Apply(null, item[1]!)));
		}
		if (p.TryGetPropertyValue("r", out var order))
		{
			var byKey = list.ToDictionary(x => x.Key, x => x.Node);
			var keys = order!.AsArray().Select(k => k!.ToJsonString()).ToList();
			if (keys.Count != byKey.Count) throw new InvalidDataException("array order does not match its entries");
			list = keys.Select(k => (k, byKey[k])).ToList();
		}
		if (list.Count > MaxArray) throw new InvalidDataException("array patch too large");
		foreach (var (_, node) in list) array.Add(node);
		return array;
	}
}

/// <summary>Patches between documents of one pass, keyed by the property values' identity: a value every player shares is diffed once.</summary>
public sealed class DocDiffCache
{
	private sealed class ByReference : IEqualityComparer<(JsonNode?, JsonNode?)>
	{
		public bool Equals((JsonNode?, JsonNode?) x, (JsonNode?, JsonNode?) y) => ReferenceEquals(x.Item1, y.Item1) && ReferenceEquals(x.Item2, y.Item2);

		public int GetHashCode((JsonNode?, JsonNode?) x) => HashCode.Combine(RuntimeHelpers.GetHashCode(x.Item1), RuntimeHelpers.GetHashCode(x.Item2));
	}

	private readonly Dictionary<(JsonNode?, JsonNode?), byte[]?> _patches = new(new ByReference());

	/// <summary>Call at the start of each pass so old documents are not kept alive.</summary>
	public void Clear() => _patches.Clear();

	/// <summary>The top-level patch from <paramref name="a"/> to <paramref name="b"/>; empty when they are equal, null when only a whole document will do.</summary>
	internal byte[]? Patch(JsonDoc a, JsonDoc b)
	{
		if (a.Props.Length != b.Props.Length) return null;
		var pieces = new List<(string Name, byte[] Patch)>();
		for (int i = 0; i < b.Props.Length; i++)
		{
			var (name, value) = b.Props[i];
			int j = a.Props[i].Key == name ? i : Array.FindIndex(a.Props, p => p.Key == name);
			if (j < 0) return null;
			var before = a.Props[j].Value;
			if (ReferenceEquals(before, value)) continue;
			if (!_patches.TryGetValue((before, value), out byte[]? piece))
				_patches[(before, value)] = piece = JsonPatch.Diff(before, value) is { } patch ? JsonSerializer.SerializeToUtf8Bytes(patch) : null;
			if (piece != null) pieces.Add((name, piece));
		}
		if (pieces.Count == 0) return Array.Empty<byte>();
		return JsonDoc.Write(w =>
		{
			w.WriteStartObject();
			w.WriteStartObject("p");
			foreach (var (name, piece) in pieces)
			{
				w.WritePropertyName(name);
				w.WriteRawValue(piece, skipInputValidation: true);
			}
			w.WriteEndObject();
			w.WriteEndObject();
		});
	}
}

/// <summary>
/// <see cref="SyncMsg.RosterSync"/>. Host to client: [47][kind][flags][seq u32][base u32][JSON], a whole document
/// (flag 1) or a patch from document <c>base</c>. Client to host: [47][kind] asks for the whole document again.
/// </summary>
public static class RosterSync
{
	public const byte Full = 1;

	public static bool IsKind(byte kind) => kind is (byte)SyncMsg.PlayerTable or (byte)SyncMsg.SocialSnapshot or (byte)SyncMsg.AdminState or (byte)SyncMsg.PlayerNames;

	public static byte[] Encode(byte kind, bool full, uint seq, uint baseSeq, byte[] json)
	{
		var w = new WireWriter(json.Length + 12).U8((byte)SyncMsg.RosterSync).U8(kind).U8(full ? Full : (byte)0).U32(seq).U32(baseSeq);
		w.Bytes(json);
		return w.ToArray();
	}

	public static byte[] Request(byte kind) => new[] { (byte)SyncMsg.RosterSync, kind };

	/// <summary>Picks the lane: small messages go reliable-direct, big ones through bulk (compressed, fragmented).</summary>
	public static Channel LaneOf(byte[] data) => data.Length > 8192 ? Channel.Bulk : Channel.Control;
}

/// <summary>Host side, one per client and message kind: what that client has and the next message to bring it up to date.</summary>
public sealed class DocState
{
	private uint _seq;

	private JsonDoc? _last;

	private bool _full = true;

	public long FullsSent, PatchesSent;

	public void RequestFull()
	{
		lock (this) _full = true;
	}

	/// <summary>The <see cref="RosterSync"/> message for a client that takes patches, or null when nothing changed.</summary>
	public byte[]? Next(byte kind, JsonDoc doc, DocDiffCache diffs)
	{
		lock (this)
		{
			byte[]? patch = _full || _last == null ? null : diffs.Patch(_last, doc);
			if (patch is { Length: 0 })
			{
				_last = doc;
				return null;
			}
			bool full = patch == null || patch.Length >= doc.Json.Length;
			uint seq = _seq + 1;
			byte[] data = RosterSync.Encode(kind, full, seq, _seq, full ? doc.Json : patch!);
			if (full) FullsSent++; else PatchesSent++;
			_seq = seq;
			_last = doc;
			_full = false;
			return data;
		}
	}

	/// <summary>For a client without patch support: true when <paramref name="doc"/> differs from what it last got (then it is sent whole).</summary>
	public bool Changed(JsonDoc doc, DocDiffCache diffs)
	{
		lock (this)
		{
			bool changed = _full || _last == null || diffs.Patch(_last, doc) is not { Length: 0 };
			_last = doc;
			_full = false;
			if (changed) FullsSent++;
			return changed;
		}
	}
}

/// <summary>Client side, one per message kind: the document as patched so far. Patches that overtake their base (bulk and direct lanes are not ordered against each other) wait for it.</summary>
public sealed class DocInbox
{
	private const int MaxPending = 64;

	private JsonObject? _doc;

	private uint _seq;

	private bool _wantFull, _asked;

	private readonly List<(uint Base, uint Seq, byte[] Json)> _pending = new();

	public long Fulls, Patches, Failures;

	/// <summary>The updated document, or null when this message changed nothing that can be shown yet.</summary>
	public JsonObject? Receive(ReadOnlySpan<byte> data)
	{
		var r = new WireReader(data.Slice(2));
		bool full = (r.U8() & RosterSync.Full) != 0;
		uint seq = r.U32();
		uint baseSeq = r.U32();
		byte[] json = r.Rest().ToArray();
		try
		{
			if (full)
			{
				if (_doc != null && (int)(seq - _seq) <= 0) return null;
				_doc = JsonNode.Parse(json)!.AsObject();
				_seq = seq;
				_wantFull = _asked = false;
				Fulls++;
			}
			else if (_doc != null && baseSeq == _seq)
			{
				Patch(seq, json);
			}
			else
			{
				if (_doc == null || (int)(baseSeq - _seq) > 0) _pending.Add((baseSeq, seq, json));
				if (_pending.Count > MaxPending) { _pending.Clear(); _wantFull = true; }
				return null;
			}
			_pending.RemoveAll(x => (int)(x.Base - _seq) < 0);
			for (int i = _pending.FindIndex(x => x.Base == _seq); i >= 0; i = _pending.FindIndex(x => x.Base == _seq))
			{
				var next = _pending[i];
				_pending.RemoveAt(i);
				Patch(next.Seq, next.Json);
			}
			return _doc;
		}
		catch (Exception)
		{
			_doc = null;
			_pending.Clear();
			_wantFull = true;
			Failures++;
			return null;
		}
	}

	private void Patch(uint seq, byte[] json)
	{
		JsonPatch.Apply(_doc, JsonNode.Parse(json)!);
		_seq = seq;
		Patches++;
	}

	/// <summary>True once each time the document is lost and the host should send it whole.</summary>
	public bool TakeFullRequest()
	{
		if (!_wantFull || _asked) return false;
		_asked = true;
		return true;
	}
}

/// <summary>The player table as a document.</summary>
public sealed class PlayerTableDoc
{
	public List<PlayerInfo> Players { get; set; } = new();
}

/// <summary>The name tag roster as a document.</summary>
public sealed class PlayerNamesDoc
{
	public uint Version { get; set; }

	public List<PlayerTag> Tags { get; set; } = new();
}
