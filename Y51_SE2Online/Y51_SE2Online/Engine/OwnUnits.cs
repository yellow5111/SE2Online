using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Definitions;
using Keen.VRage.Library.Serialization;
using Keen.VRage.Library.Serialization.Binary;
using Keen.VRage.Library.Serialization.Binary.Archive;
using Keen.VRage.Library.UI;
using Keen.VRage.Library.Utils;
using Keen.VRage.Multiplayer.NetworkStories;
using Keen.VRage.Multiplayer.Replications;
using Keen.VRage.Multiplayer.Replications.DataOffloading;
using Keen.VRage.Multiplayer.Transport;
using Y51_SE2Online.SyncEngine;

namespace Y51_SE2Online.Engine;

/// <summary>
/// Values that cross as the game's own per-type serializers write them (a value's type knows its format, the way a save
/// does), referencing objects by SE2 Online's ids: one archive of numbered chunks per unit. The units around them
/// (signals, stories, object batches) are SE2 Online's own format (<see cref="OwnUnits"/>).
/// </summary>
internal static class OwnArchive
{
	/// <summary>A value and the type it is declared as (the slot it fills), which picks how it is written.</summary>
	public readonly record struct Leaf(Type Declared, object? Value);

	private static string Chunk(int i) => i == 0 ? "[Main]" : i.ToString();

	private static readonly ConcurrentDictionary<Type, Func<object?, object>> Boxers = new();

	private static readonly System.Reflection.MethodInfo BoxDef = typeof(OwnArchive).GetMethod(nameof(BoxAs), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;

	// Each value goes as a member of its declared type, as a field of a saved object does: references to entities,
	// components and replicated models are written by name (their network id), a value of a derived type with its type.
	private static object Box(Leaf leaf) => Boxers.GetOrAdd(leaf.Declared, t => BoxDef.MakeGenericMethod(t).CreateDelegate<Func<object?, object>>())(leaf.Value);

	private static object BoxAs<T>(object? value) => new StoryFieldValue<T> { Value = value is T typed ? typed : default! };

	public static byte[] Write(List<Leaf> leaves, ServiceLocatorContext.Builder services)
	{
		if (leaves.Count == 0) return Array.Empty<byte>();
		using var stream = new MemoryStream();
		using var context = new SerializationContext(stream, "SE2 Online values", services);
		using (var writer = new BinaryArchiveWriter(context))
			for (int i = 0; i < leaves.Count; i++) writer.AddChunk<object>(Chunk(i), Box(leaves[i]));
		return stream.ToArray();
	}

	/// <summary>Every value of an archive; one that cannot be read here (it names an object this side does not have) is a
	/// failure of its own, the rest still read.</summary>
	public static Leaves Read(byte[] bytes, ServiceLocatorContext.Builder services) => Read(new ArraySegment<byte>(bytes), services);

	/// <summary>The archive read where it lies inside a received unit: a big world's batch (52 MiB) is not copied out first.</summary>
	public static Leaves Read(ArraySegment<byte> bytes, ServiceLocatorContext.Builder services)
	{
		if (bytes.Count == 0) return new Leaves(Array.Empty<object?>(), Array.Empty<Exception?>());
		using var stream = new MemoryStream(bytes.Array!, bytes.Offset, bytes.Count, writable: false);
		using var context = new SerializationContext(stream, "SE2 Online values", services);
		CheckSizes(bytes, context);
		stream.Position = 0;
		using var reader = new BinaryArchiveReader(context);
		int count = reader.GetChunks().Count;
		var values = new object?[count];
		var errors = new Exception?[count];
		var skipped = new bool[count];
		for (int i = 0; i < count; i++)
		{
			int misses = OwnIds.SkipMisses;
			try
			{
				values[i] = reader.ReadChunk<object>(Chunk(i)) is IStoryFieldValue box ? box.Boxed : throw new InvalidDataException("a value of another kind");
			}
			catch (Exception ex) when (ex.CanHandle()) { errors[i] = ex; }
			skipped[i] = OwnIds.SkipMisses != misses;
		}
		return new Leaves(values, errors, skipped);
	}

	// The game's reader allocates every table and chunk at the size the archive claims before it reads a byte: a few bytes
	// from a peer could claim gigabytes. Every entry must lie inside the archive, and nothing may claim to inflate past a
	// bounded ratio (SE2 Online writes its values uncompressed; the game's writer may pack its type table).
	private const long MaxInflated = 32L << 20;

	// Archives from the server this game joined (read on the joined player's inbound job, FromServer): a big world's initial
	// object batch is one archive (52 MiB for 136k entities), which the players' cap refused after the whole download. As
	// large as a bulk message from the host may be (TransportOptions.MaxBulkMessageBytes); every entry check stays.
	internal const long MaxInflatedFromServer = 256L << 20;

	[ThreadStatic] private static bool _fromServer;

	internal static void BeginFromServer() => _fromServer = true;

	internal static void EndFromServer() => _fromServer = false;

	internal static long ArchiveCap(bool fromServer) => fromServer ? MaxInflatedFromServer : MaxInflated;

	// What the players' frames of one inbound job may make the game's reader allocate (its tables come from the thread's
	// bump allocator, freed once per job). A transport stops taking frames once it is spent and leaves the rest for the
	// next job; one frame already taken may overrun it by at most one more budget. Outside a budget nothing is counted.
	[ThreadStatic] private static long _budget;
	[ThreadStatic] private static bool _budgeted;

	internal static void BeginBudget(long bytes) => (_budget, _budgeted) = (bytes, true);

	internal static void EndBudget() => _budgeted = false;

	internal static bool BudgetSpent => _budgeted && _budget <= 0;

	internal static long BudgetLeft => _budgeted ? _budget : long.MaxValue;

	private static void Spend(long bytes)
	{
		if (_budgeted && (_budget -= bytes) < -MaxInflated) throw new InvalidDataException("values: more than one inbound job's budget");
	}
	// ponytail: a collection length inside a chunk is still the game's to bound: a peer can make one value allocate up to
	// what a length claims before its read fails (caught, garbage afterwards; the game's own netcode had the same). A
	// bound needs a hook in the game's collection serializers, which are precompiled and inline their length reads.

	private static void CheckSizes(ArraySegment<byte> bytes, SerializationContext context)
	{
		using var probe = new MemoryStream(bytes.Array!, bytes.Offset, bytes.Count, writable: false);
		var header = BinaryArchiveParser.TryReadHeader(probe, out var validation) ?? throw new InvalidDataException("values: " + validation);
		long limit = Math.Min(ArchiveCap(_fromServer && !_budgeted), bytes.Count * 16L + 4096), total = 0;
		// Tables may be packed (the game's writer packs its type table) but inflate at most 16:1; chunks are written unpacked.
		void Entry(long offset, long size, long inflated, CompressionType compression, bool chunk)
		{
			if (offset < 0 || size < 0 || offset + size > bytes.Count) throw new InvalidDataException($"values: an entry at {offset} of {size} bytes in {bytes.Count}");
			if (chunk && compression != CompressionType.None && inflated != 0) throw new InvalidDataException("values: a packed chunk");
			long most = compression == CompressionType.None ? size : Math.Min(limit, size * 16 + 4096);
			if (inflated < 0 || inflated > most) throw new InvalidDataException($"values: an entry claims {inflated} bytes from {size}");
			if ((total += inflated) > limit) throw new InvalidDataException("values: the archive claims more than " + limit + " bytes");
		}
		Entry(header.BundleTable.Offset, header.BundleTable.EntrySize, header.BundleTable.UncompressedSize, header.BundleTable.CompressionType, false);
		Entry(header.TypeTable.Offset, header.TypeTable.EntrySize, header.TypeTable.UncompressedSize, header.TypeTable.CompressionType, false);
		Entry(header.ChunkTable.Offset, header.ChunkTable.EntrySize, header.ChunkTable.UncompressedSize, header.ChunkTable.CompressionType, false);
		Entry(header.MainChunk.Offset, header.MainChunk.EntrySize, header.MainChunk.UncompressedSize, header.MainChunk.CompressionType, true);
		// The type table is held about four times over (this check's copy and both readers' offset buffers), the chunk table twice.
		Spend(total + 3 * header.TypeTable.UncompressedSize + header.ChunkTable.UncompressedSize);
		if (header.ChunkCount < 0 || header.ChunkCount > 1 << 16 || header.BundleCount < 0 || header.BundleCount > 1024) throw new InvalidDataException("values: " + header.ChunkCount + " chunks");
		CheckTypeTable(bytes, header);
		if (!header.HasChunkTable) return;
		context.Stream.Position = 0;
		var chunks = new List<KeyValuePair<string, ChunkArchiveEntry>>(header.ChunkCount);
		new BinaryArchiveParser(context).ReadChunkTable(in header, chunks);
		long before = total;
		foreach (var (_, chunk) in chunks) Entry(chunk.Offset, chunk.EntrySize, chunk.UncompressedSize, chunk.CompressionType, true);
		Spend(total - before);
	}

	// Deepest chain of type names and schemas the game's resolver may recurse through (it recurses once per link).
	private const int MaxTypeDepth = 48;

	/// <summary>
	/// The game resolves an archive's type table recursively and remembers an entry only once resolved: an entry naming
	/// itself (or a long chain) would recurse until the stack overflows, which ends the process. Every reference must
	/// name an entry that exists, without cycles and no deeper than <see cref="MaxTypeDepth"/>; the counts must fit the
	/// table (the game allocates by them first).
	/// </summary>
	private static void CheckTypeTable(ArraySegment<byte> bytes, in ArchiveHeader header)
	{
		var entry = header.TypeTable;
		if (entry.UncompressedSize == 0) return;
		byte[] table;
		if (entry.CompressionType == CompressionType.None) table = bytes.AsSpan((int)entry.Offset, (int)entry.UncompressedSize).ToArray();
		else
		{
			table = new byte[entry.UncompressedSize];
			using var packed = new MemoryStream(bytes.Array!, bytes.Offset + (int)entry.Offset, (int)entry.EntrySize, writable: false);
			using Stream unpack = entry.CompressionType switch
			{
				CompressionType.Brotli => new BrotliStream(packed, CompressionMode.Decompress),
				CompressionType.ZLib => new ZLibStream(packed, CompressionMode.Decompress),
				_ => throw new InvalidDataException("values: a type table compressed as " + entry.CompressionType),
			};
			unpack.ReadExactly(table);
		}
		CheckTypeTable(table, header.Version);
	}

	/// <summary>
	/// A plain type name in a type table. The game parses it recursively (once per '&lt;') and loads the assembly an '@Name:'
	/// prefix names; its own writer puts arrays and constructed generics in entries of their own, so a plain name has no
	/// brackets or commas, and an assembly it names is one this game has loaded.
	/// </summary>
	private static void CheckName(string name)
	{
		if (name.Length == 0 || name.Length > 1024 || name.AsSpan().IndexOfAny("<>[],*&") >= 0) throw new InvalidDataException("values: a type name the game's writer does not write");
		if (name[0] != '@') return;
		int colon = name.IndexOf(':');
		if (colon < 2 || OwnUnits.LoadedAssembly(name[1..colon]) == null) throw new InvalidDataException("values: a type name naming an assembly this game has not loaded");
	}

	internal static void CheckTypeTable(byte[] table, uint version)
	{
		if (table.Length < 8) throw new InvalidDataException("values: a short type table");
		int names = BitConverter.ToInt32(table, 0), schemas = BitConverter.ToInt32(table, 4);
		if (names < 0 || schemas < 0 || (long)names + schemas > table.Length) throw new InvalidDataException($"values: a type table claims {names} names and {schemas} schemas in {table.Length} bytes");
		var links = new List<int>[names + schemas];
		var reader = new Keen.VRage.Library.Serialization.Binary.TypeModel.TypeModelReader(new VRageBinaryReader(table), version);
		try
		{
			// Node i < names is name i; names + j is schema j. A predefined index (below 512) is a leaf.
			void Name(List<int> to, int index)
			{
				if (index < 512) return;
				if (index - 512 >= names) throw new InvalidDataException("values: a type name refers past the table");
				to.Add(index - 512);
			}
			void Schema(List<int> to, int index)
			{
				if (index < 512) return;
				if (index - 512 >= schemas) throw new InvalidDataException("values: a type schema refers past the table");
				to.Add(names + index - 512);
			}
			for (int i = 0; i < names; i++)
			{
				var to = links[i] = new List<int>(2);
				switch (reader.GetNameKind(i))
				{
				case Keen.VRage.Library.Serialization.Binary.TypeModel.NameEntryKind.Name: CheckName(reader.ReadSimpleTypeName(i).Name); break;
				case Keen.VRage.Library.Serialization.Binary.TypeModel.NameEntryKind.Array: Name(to, reader.ReadArrayType(i).ElementType); break;
				case Keen.VRage.Library.Serialization.Binary.TypeModel.NameEntryKind.Generic:
					var generic = reader.ReadConstructedGenericType(i);
					Name(to, generic.GenericTypeDefinition);
					foreach (var parameter in generic.GenericTypeParameters) Name(to, parameter);
					break;
				}
			}
			for (int j = 0; j < schemas; j++)
			{
				var to = links[names + j] = new List<int>(3);
				switch (reader.GetSchemaKind(j))
				{
				case Keen.VRage.Library.Serialization.Binary.TypeModel.SchemaEntryKind.Atomic:
					var atomic = reader.ReadAtomic(j);
					Name(to, atomic.Name);
					if (!atomic.NameOrProxyType.IsNull) Schema(to, atomic.NameOrProxyType);
					break;
				case Keen.VRage.Library.Serialization.Binary.TypeModel.SchemaEntryKind.CompositeDefinition: Name(to, reader.ReadCompositeName(j)); break;
				case Keen.VRage.Library.Serialization.Binary.TypeModel.SchemaEntryKind.Dictionary:
					var dictionary = reader.ReadDictionary(j);
					Name(to, dictionary.Name); Schema(to, dictionary.KeySchema); Schema(to, dictionary.ValueSchema);
					break;
				case Keen.VRage.Library.Serialization.Binary.TypeModel.SchemaEntryKind.List:
					var list = reader.ReadList(j);
					Name(to, list.Name); Schema(to, list.ElementSchema);
					break;
				case Keen.VRage.Library.Serialization.Binary.TypeModel.SchemaEntryKind.Set:
					var set = reader.ReadSet(j);
					Name(to, set.Name); Schema(to, set.ElementSchema);
					break;
				}
			}
		}
		finally { reader.Dispose(); }
		// Depth of every node without recursing here either: 0 unknown, -1 on the path (a cycle), else 1 + deepest link.
		var depth = new int[links.Length];
		var path = new Stack<(int Node, int Next)>();
		for (int start = 0; start < links.Length; start++)
		{
			if (depth[start] != 0) continue;
			depth[start] = -1;
			path.Push((start, 0));
			while (path.Count > 0)
			{
				var (node, next) = path.Pop();
				if (next < links[node].Count)
				{
					path.Push((node, next + 1));
					int link = links[node][next];
					if (depth[link] == -1) throw new InvalidDataException("values: the type table refers to itself");
					if (depth[link] == 0) { depth[link] = -1; path.Push((link, 0)); }
					continue;
				}
				int deepest = 0;
				foreach (int link in links[node]) deepest = Math.Max(deepest, depth[link]);
				if ((depth[node] = deepest + 1) > MaxTypeDepth) throw new InvalidDataException("values: the type table nests deeper than " + MaxTypeDepth);
			}
		}
	}

	public sealed class Leaves(object?[] values, Exception?[] errors, bool[]? skipped = null)
	{
		public int Count => values.Length;

		/// <summary>Whether a skip-on-fail reference inside the value named an object this side does not have.</summary>
		public bool Skipped(int index) => skipped != null && (uint)index < (uint)skipped.Length && skipped[index];

		public object? Get(int index)
		{
			if ((uint)index >= (uint)values.Length) throw new InvalidDataException($"value #{index} of {values.Length}");
			if (errors[index] is { } error) throw new LeafException(error);
			return values[index];
		}
	}

	/// <summary>A value of the unit could not be read on this side; the unit is skipped alone.</summary>
	public sealed class LeafException(Exception inner) : Exception(Innermost(inner), inner);

	public static string Innermost(Exception ex)
	{
		while (ex.InnerException != null) ex = ex.InnerException;
		return ex.GetType().Name + ": " + ex.Message;
	}
}

/// <summary>SE2 Online's units of the engine wire: signals, stories and object batches, with their values in an <see cref="OwnArchive"/>.</summary>
internal static class OwnUnits
{
	// --- signals: [u16 n]{[string name][u8 argc]{[u8 kind] kind data}} [archive] ---

	public static byte[] WriteSignals(IReadOnlyList<OwnSignal> signals, OwnIds ids, ServiceLocatorContext.Builder services)
	{
		var leaves = new List<OwnArchive.Leaf>();
		using var ms = new MemoryStream();
		using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
		{
			w.Write((ushort)signals.Count);
			foreach (OwnSignal signal in signals)
			{
				w.Write(signal.Name);
				w.Write((byte)signal.Args.Length);
				foreach (OwnArg arg in signal.Args) WriteArg(w, arg, ids, leaves);
			}
			WriteArchive(w, leaves, services);
		}
		return ms.ToArray();
	}

	private static void WriteArg(BinaryWriter w, OwnArg arg, OwnIds ids, List<OwnArchive.Leaf> leaves)
	{
		w.Write((byte)arg.Kind);
		switch (arg.Kind)
		{
		case ArgKind.Null:
			break;
		case ArgKind.Object:
		case ArgKind.Inline:
		case ArgKind.Span:
			w.Write(Leaf(leaves, typeof(object), arg.Value!));
			break;
		case ArgKind.Nullable:
			w.Write(TypeName(Nullable.GetUnderlyingType(arg.Inline!)!));
			w.Write(arg.Value != null);
			if (arg.Value != null) w.Write(Leaf(leaves, typeof(object), arg.Value));
			break;
		case ArgKind.Definition:
			w.Write(((Definition)arg.Value!).Guid.ToByteArray());
			break;
		case ArgKind.Entity:
			w.Write((ids.TryGetId(arg.Value!) ?? ids.TryGetRetiredId(arg.Value!) ?? throw new InvalidOperationException("an entity without a network id")).Value);
			break;
		default:
			throw new InvalidOperationException("unknown argument kind " + arg.Kind);
		}
	}

	/// <summary>The signals of a block; one whose values cannot be read here is left out (counted in <paramref name="unreadable"/>).</summary>
	public static List<OwnSignal> ReadSignals(byte[] bytes, OwnIds ids, ServiceLocatorContext.Builder services, out int unreadable, out string? firstError)
	{
		unreadable = 0;
		firstError = null;
		using var r = new BinaryReader(new MemoryStream(bytes, writable: false), Encoding.UTF8);
		int count = r.ReadUInt16();
		var raw = new List<(string Name, (ArgKind Kind, long Leaf, Type? Type, Guid Def)[] Args)>(count);
		for (int i = 0; i < count; i++)
		{
			string name = r.ReadString();
			var args = new (ArgKind, long, Type?, Guid)[r.ReadByte()];
			for (int a = 0; a < args.Length; a++) args[a] = ReadArg(r);
			raw.Add((name, args));
		}
		var leaves = ReadArchive(r, services);
		var signals = new List<OwnSignal>(count);
		foreach (var (name, args) in raw)
		{
			try
			{
				var resolved = new OwnArg[args.Length];
				for (int a = 0; a < args.Length; a++) resolved[a] = Resolve(args[a], ids, leaves);
				signals.Add(new OwnSignal(name, resolved));
			}
			catch (Exception ex) when (ex.CanHandle())
			{
				unreadable++;
				firstError ??= OwnSignals.Short(name) + ": " + (ex is OwnArchive.LeafException ? ex.Message : OwnArchive.Innermost(ex));
			}
		}
		return signals;
	}

	private static (ArgKind, long, Type?, Guid) ReadArg(BinaryReader r)
	{
		var kind = (ArgKind)r.ReadByte();
		switch (kind)
		{
		case ArgKind.Null:
			return (kind, -1, null, default);
		case ArgKind.Object:
		case ArgKind.Inline:
		case ArgKind.Span:
			return (kind, r.ReadInt32(), null, default);
		case ArgKind.Nullable:
		{
			Type? type = ValueType(r.ReadString());
			int leaf = r.ReadBoolean() ? r.ReadInt32() : -1;
			return (kind, leaf, type, default);
		}
		case ArgKind.Definition:
			return (kind, -1, null, new Guid(r.ReadBytes(16)));
		case ArgKind.Entity:
			return (kind, (long)r.ReadUInt64(), null, default);
		default:
			throw new InvalidDataException("unknown argument kind " + (byte)kind);
		}
	}

	private static OwnArg Resolve((ArgKind Kind, long Leaf, Type? Type, Guid Def) arg, OwnIds ids, OwnArchive.Leaves leaves)
	{
		switch (arg.Kind)
		{
		case ArgKind.Entity:
			return new OwnArg(ArgKind.Entity, ids.TryGetObject(new NetworkId((ulong)arg.Leaf)) as Entity
				?? throw new InvalidDataException($"entity #{arg.Leaf} is not in this game"));
		case ArgKind.Null:
			return new OwnArg(ArgKind.Null, null);
		case ArgKind.Object:
		{
			object value = leaves.Get((int)arg.Leaf) ?? throw new InvalidDataException("an object argument read as null");
			if (leaves.Skipped((int)arg.Leaf)) throw new InvalidDataException("a skip-on-fail reference names an object this game does not have");
			return new OwnArg(ArgKind.Object, value);
		}
		case ArgKind.Inline:
		{
			object value = leaves.Get((int)arg.Leaf) ?? throw new InvalidDataException("an inline argument read as null");
			if (!OwnSignals.Raw.IsUnmanaged(value.GetType())) throw new InvalidDataException(value.GetType().Name + " is not an inline value");
			return new OwnArg(ArgKind.Inline, value, value.GetType());
		}
		case ArgKind.Nullable:
		{
			Type type = arg.Type ?? throw new InvalidDataException("a nullable argument of a type this game does not have");
			object? value = arg.Leaf < 0 ? null : leaves.Get((int)arg.Leaf);
			if (value != null && value.GetType() != type) throw new InvalidDataException($"a {type.Name}? argument read as {value.GetType().Name}");
			return new OwnArg(ArgKind.Nullable, value, typeof(Nullable<>).MakeGenericType(type));
		}
		case ArgKind.Definition:
			return new OwnArg(ArgKind.Definition, ObjectCodec.FindDefinition(arg.Def, typeof(Definition)) ?? throw new InvalidDataException($"definition {arg.Def} is not loaded here"));
		case ArgKind.Span:
		{
			if (leaves.Get((int)arg.Leaf) is not Array array || array.GetType().GetElementType() is not { } element || !OwnSignals.Raw.IsUnmanaged(element))
				throw new InvalidDataException("a span argument that is not an array of values");
			return new OwnArg(ArgKind.Span, array, element);
		}
		default:
			throw new InvalidDataException("unknown argument kind " + (byte)arg.Kind);
		}
	}

	// --- stories ---
	// move: [u8 1][string type][i32 state][u16 n]{[u16 field][u8 has][i32 leaf]} [archive]
	// completion: [u8 2][i32 leaf] [archive]  (the leaf is the game's completion: a result or a fault)

	public static byte[] WriteMove(OwnStoryMove move, ServiceLocatorContext.Builder services)
	{
		var leaves = new List<OwnArchive.Leaf>();
		using var ms = new MemoryStream();
		using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
		{
			w.Write((byte)1);
			w.Write(move.TypeName);
			w.Write(move.State);
			w.Write((ushort)move.Fields.Count);
			foreach (var (index, declared, value) in move.Fields)
			{
				w.Write((ushort)index);
				w.Write(value != null);
				if (value != null) w.Write(Leaf(leaves, declared, value));
			}
			WriteArchive(w, leaves, services);
		}
		return ms.ToArray();
	}

	public static byte[] WriteCompletion(IStoryCompletion completion, ServiceLocatorContext.Builder services)
	{
		var leaves = new List<OwnArchive.Leaf>();
		using var ms = new MemoryStream();
		using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
		{
			w.Write((byte)2);
			w.Write(Leaf(leaves, typeof(object), completion));
			WriteArchive(w, leaves, services);
		}
		return ms.ToArray();
	}

	/// <summary>A story unit: a move (with its id) or a completion; throws when it cannot be read here.</summary>
	public static object ReadStory(StoryId id, byte[] bytes, ServiceLocatorContext.Builder services)
	{
		using var r = new BinaryReader(new MemoryStream(bytes, writable: false), Encoding.UTF8);
		byte kind = r.ReadByte();
		if (kind == 2)
		{
			int leaf = r.ReadInt32();
			var leaves = ReadArchive(r, services);
			return leaves.Get(leaf) as IStoryCompletion ?? throw new InvalidDataException("a story completion that is none");
		}
		if (kind != 1) throw new InvalidDataException("unknown story unit " + kind);
		var move = new OwnStoryMove { Id = id, TypeName = r.ReadString(), State = r.ReadInt32() };
		int n = r.ReadUInt16();
		var fields = new (int Index, int Leaf)[n];
		for (int i = 0; i < n; i++)
		{
			int index = r.ReadUInt16();
			fields[i] = (index, r.ReadBoolean() ? r.ReadInt32() : -1);
		}
		var values = ReadArchive(r, services);
		foreach (var (index, leaf) in fields)
		{
			// The game's reader leaves a skip-on-fail field naming a removed object as null and the story then dereferences it
			// (a quick transfer of an item the server had just replaced faulted with a NullReferenceException). Unreadable,
			// so the caller answers it as skipped, which is the wrapper's contract.
			if (leaf >= 0 && values.Skipped(leaf)) throw new InvalidDataException($"story {OwnStories.Short(move.TypeName)}: a skip-on-fail field names an object this game does not have");
			move.Fields.Add((index, typeof(object), leaf < 0 ? null : values.Get(leaf)));
		}
		return move;
	}

	/// <summary>A story move's short name for a log line, when it names a network story this game has; else null.</summary>
	public static string? StoryName(byte[] bytes)
	{
		try
		{
			using var r = new BinaryReader(new MemoryStream(bytes, writable: false), Encoding.UTF8);
			if (r.ReadByte() != 1) return null;
			return OwnStories.Resolve(r.ReadString()) is { } type ? OwnStories.Short(type.FullName ?? type.Name) : null;
		}
		catch (Exception) { return null; }
	}

	// --- object batches ---

	public enum Op : byte { Batch = 1, Destroy = 2, Ack = 3 }

	public enum StubKind : byte { Entity = 0, Observable = 1, Collection = 2 }

	public readonly record struct Stub(StubKind Kind, ulong Id, string? ObservableType);

	// [u8 1][u64 batch][string object type][string payload type][i32 n]{[u8 kind][u64 id][string type if observable]} [archive: payload]
	public static byte[] WriteBatch(ulong batch, Type objectType, Type payloadType, IReadOnlyList<Stub> stubs, object payload, ServiceLocatorContext.Builder services)
	{
		var leaves = new List<OwnArchive.Leaf> { new(payloadType, payload) };
		using var ms = new MemoryStream();
		using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
		{
			w.Write((byte)Op.Batch);
			w.Write(batch);
			w.Write(TypeName(objectType));
			w.Write(TypeName(payloadType));
			w.Write(stubs.Count);
			foreach (var stub in stubs)
			{
				w.Write((byte)stub.Kind);
				w.Write(stub.Id);
				if (stub.Kind == StubKind.Observable) w.Write(stub.ObservableType!);
			}
			WriteArchive(w, leaves, services);
		}
		return ms.ToArray();
	}

	// [u8 2][u64 batch][string object type][i32 n]{[u64 id]}
	public static byte[] WriteDestroy(ulong batch, Type objectType, IReadOnlyList<ulong> ids)
	{
		using var ms = new MemoryStream();
		using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
		{
			w.Write((byte)Op.Destroy);
			w.Write(batch);
			w.Write(TypeName(objectType));
			w.Write(ids.Count);
			foreach (ulong id in ids) w.Write(id);
		}
		return ms.ToArray();
	}

	// [u8 3][u64 batch]
	public static byte[] WriteAck(ulong batch)
	{
		var bytes = new byte[9];
		bytes[0] = (byte)Op.Ack;
		BitConverter.TryWriteBytes(bytes.AsSpan(1), batch);
		return bytes;
	}

	public static Op OpOf(byte[] unit) => unit.Length > 0 ? (Op)unit[0] : throw new InvalidDataException("an empty replication unit");

	public static ulong AckOf(byte[] unit) => unit.Length == 9 && unit[0] == (byte)Op.Ack ? BitConverter.ToUInt64(unit, 1) : throw new InvalidDataException("not an acknowledgement");

	/// <summary>A batch's header (the objects it creates); its payload is read once their stubs exist (<see cref="ReadPayload"/>).</summary>
	public static (ulong Batch, Type Object, Type Payload, Stub[] Stubs, int ArchiveAt) ReadBatchHeader(byte[] unit)
	{
		using var r = new BinaryReader(new MemoryStream(unit, writable: false), Encoding.UTF8);
		if ((Op)r.ReadByte() != Op.Batch) throw new InvalidDataException("not an object batch");
		ulong batch = r.ReadUInt64();
		string objectName = r.ReadString(), payloadName = r.ReadString();
		Type objectType = ObjectType(objectName) ?? throw new InvalidDataException($"object batch of {objectName}, which this side does not replicate");
		Type payloadType = PayloadType(payloadName) ?? throw new InvalidDataException($"object batch payload {payloadName} is not known here");
		int n = r.ReadInt32();
		if (n < 0 || n > 1_000_000) throw new InvalidDataException("an object batch of " + n + " objects");
		var stubs = new Stub[n];
		for (int i = 0; i < n; i++)
		{
			var kind = (StubKind)r.ReadByte();
			ulong id = r.ReadUInt64();
			stubs[i] = new Stub(kind, id, kind == StubKind.Observable ? r.ReadString() : null);
		}
		return (batch, objectType, payloadType, stubs, (int)r.BaseStream.Position);
	}

	public static object? ReadPayload(byte[] unit, int archiveAt, ServiceLocatorContext.Builder services)
	{
		// [i32 length][archive], read in place (WriteArchive's layout; ReadArchive copies, which for a world batch is 52 MiB more).
		if (archiveAt < 0 || archiveAt > unit.Length - 4) throw new InvalidDataException("an object batch without its values");
		int length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(unit.AsSpan(archiveAt));
		if (length < 0 || length > unit.Length - archiveAt - 4) throw new InvalidDataException($"a unit claims {length} bytes of values");
		var leaves = OwnArchive.Read(new ArraySegment<byte>(unit, archiveAt + 4, length), services);
		return leaves.Count > 0 ? leaves.Get(0) : null;
	}

	public static (ulong Batch, Type Object, ulong[] Ids) ReadDestroy(byte[] unit)
	{
		using var r = new BinaryReader(new MemoryStream(unit, writable: false), Encoding.UTF8);
		if ((Op)r.ReadByte() != Op.Destroy) throw new InvalidDataException("not an object destruction");
		ulong batch = r.ReadUInt64();
		string objectName = r.ReadString();
		Type objectType = ObjectType(objectName) ?? throw new InvalidDataException($"destruction of {objectName}, which this side does not replicate");
		int n = r.ReadInt32();
		if (n < 0 || n > (unit.Length - r.BaseStream.Position) / 8) throw new InvalidDataException("a destruction of " + n + " objects");
		var ids = new ulong[n];
		for (int i = 0; i < n; i++) ids[i] = r.ReadUInt64();
		return (batch, objectType, ids);
	}

	// --- shared ---

	private static int Leaf(List<OwnArchive.Leaf> leaves, Type declared, object value)
	{
		leaves.Add(new OwnArchive.Leaf(declared, value));
		return leaves.Count - 1;
	}

	private static void WriteArchive(BinaryWriter w, List<OwnArchive.Leaf> leaves, ServiceLocatorContext.Builder services)
	{
		byte[] archive = OwnArchive.Write(leaves, services);
		w.Write(archive.Length);
		w.Write(archive);
	}

	private static OwnArchive.Leaves ReadArchive(BinaryReader r, ServiceLocatorContext.Builder services)
	{
		int length = r.ReadInt32();
		if (length < 0 || length > r.BaseStream.Length - r.BaseStream.Position) throw new InvalidDataException($"a unit claims {length} bytes of values");
		return OwnArchive.Read(r.ReadBytes(length), services);
	}

	public static string TypeName(Type type) => type.AssemblyQualifiedName ?? type.FullName ?? type.Name;

	private static readonly ConcurrentDictionary<string, Type> Known = new(StringComparer.Ordinal);
	private static readonly ConcurrentDictionary<string, byte> Unknown = new(StringComparer.Ordinal);

	/// <summary>A world (and the mods it loaded) starts: names unknown before may be known now.</summary>
	internal static void ForgetUnknown() => Unknown.Clear();

	/// <summary>
	/// A type a peer names, among the assemblies this game has loaded only (a name never makes it load or look for an
	/// assembly); only what resolves is remembered, so made-up names cost nothing that stays.
	/// </summary>
	public static Type? Find(string name)
	{
		if (Known.TryGetValue(name, out var known)) return known;
		// Generic nesting past a few levels is no type the game sends; a name that did not resolve is not looked up again.
		if (name.Length > 2048 || name.AsSpan().Count('[') > 24 || Unknown.ContainsKey(name)) return null;
		Type? type;
		try
		{
			type = Type.GetType(name, an => Loaded(an.Name), (assembly, typeName, ignoreCase) => assembly != null ? assembly.GetType(typeName, false, ignoreCase) : typeof(object).Assembly.GetType(typeName, false, ignoreCase), throwOnError: false);
		}
		catch (Exception) { return null; }
		// Only the two forms SE2 Online writes: names differing in, say, a version would each keep a copy for the same type.
		if (type != null && Known.Count < 100_000 && (string.Equals(name, TypeName(type), StringComparison.Ordinal) || string.Equals(name, type.FullName + ", " + type.Assembly.GetName().Name, StringComparison.Ordinal))) Known[name] = type;
		else if (type == null)
		{
			if (Unknown.Count >= 10_000) Unknown.Clear();
			Unknown[name] = 0;
		}
		return type;
	}

	private static System.Reflection.Assembly? Loaded(string? name) => name == null ? null : LoadedAssembly(name);

	private static readonly ConcurrentDictionary<string, System.Reflection.Assembly> Assemblies = InitAssemblies();

	private static ConcurrentDictionary<string, System.Reflection.Assembly> InitAssemblies()
	{
		var map = new ConcurrentDictionary<string, System.Reflection.Assembly>(StringComparer.Ordinal);
		AppDomain.CurrentDomain.AssemblyLoad += (_, e) => Add(map, e.LoadedAssembly);
		foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) Add(map, assembly);
		return map;
	}

	private static void Add(ConcurrentDictionary<string, System.Reflection.Assembly> map, System.Reflection.Assembly assembly)
	{
		// A collectible one (a compiled script) would be kept from unloading.
		try { if (!assembly.IsCollectible && assembly.GetName().Name is { } name) map.TryAdd(name, assembly); }
		catch (Exception) { }
	}

	/// <summary>An assembly this game has loaded, by its simple name.</summary>
	internal static System.Reflection.Assembly? LoadedAssembly(string name) => Assemblies.GetValueOrDefault(name);

	// Only what the game's replication creates on a player's side.
	public static Type? ObjectType(string name) => Find(name) is { } t && (t == typeof(Entity) || t == typeof(ObservableObject) || t == typeof(IObservedCollection) || t == typeof(BlobReplicationData.Stub)) ? t : null;

	public static Type? PayloadType(string name) => Find(name) is { } t && !t.IsAbstract && !t.IsPointer && !t.IsByRef && Keen.VRage.Multiplayer.Data.ReplicatedDataValidator.IsSerializable(t) ? t : null;

	private static Type? ValueType(string name) => Find(name) is { } t && OwnSignals.Raw.IsUnmanaged(t) && Nullable.GetUnderlyingType(t) == null ? t : null;
}
