using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.Specialized;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Keen.Game2.Simulation.GameSystems.BlockPlacement;
using Keen.Game2.Simulation.GameSystems.Toolbars.Actions;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Builders;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Definitions;
using Keen.VRage.Library.Serialization;
using Keen.VRage.Library.UI;
using Keen.VRage.Library.Utils;
using Keen.VRage.Multiplayer;
using Keen.VRage.Multiplayer.SecureIds;
using Keen.VRage.Multiplayer.Sessions;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

internal static class ObjectCodec
{
	private delegate bool TryAnyFn(DefinitionManager dm, Guid id, out Definition? definition);

	private static readonly ConcurrentDictionary<Type, FieldInfo[]> FieldsCache = new();

	private static readonly ConcurrentDictionary<Type, Func<object, byte[]>> ToBytesCache = new();

	private static readonly ConcurrentDictionary<Type, Func<byte[], object>> FromBytesCache = new();

	private static readonly ConcurrentDictionary<Type, bool> SerializableCache = new();

	private static readonly HashSet<Type> PlainDataClasses = new() { typeof(BlockPlacerEntityBundle) };

	private static Func<DefinitionManager, Guid, Definition?>? _tryAny;

	private static bool _tryAnyResolved;

	public const string DictPrefix = "Dict:";

	public const string SetPrefix = "Set:";

	public const string ReleasedModel = "was already released by the host";

	private static readonly MethodInfo IsRefMethod = typeof(RuntimeHelpers).GetMethod("IsReferenceOrContainsReferences");

	private static readonly ConcurrentDictionary<Type, bool> RefCache = new();

	private static readonly MethodInfo SerializeGeneric = typeof(SerializationHelper).GetMethods(BindingFlags.Static | BindingFlags.Public).First((MethodInfo m) => m.Name == "Serialize" && m.IsGenericMethodDefinition && m.GetParameters().Length == 3 && m.GetParameters()[0].ParameterType == typeof(SerializationContext));

	private static readonly MethodInfo DeserializeGeneric = typeof(SerializationHelper).GetMethods(BindingFlags.Static | BindingFlags.Public).First((MethodInfo m) => m.Name == "Deserialize" && m.IsGenericMethodDefinition && m.GetParameters().Length == 2 && m.GetParameters()[0].ParameterType == typeof(SerializationContext));

	internal static bool DefinitionLookupBound => TryAny() != null;

	public static string TypeName(Type t) => t.FullName + ", " + t.Assembly.GetName().Name;

	static ObjectCodec()
	{
		WireTypes.Log ??= GameLog.Instance;
	}

	// Story state machines named by the peer (HostStories, ClientStories, StoryState).
	public static Type? FindType(string name) => WireTypes.Resolve(name, typeof(IAsyncStateMachine), construct: true);

	// The only classes Encode copies field by field (see EncodeFields callers).
	private static bool IsFieldCopied(Type t) => IsCompilerGenerated(t) || t.IsValueType || t == typeof(ReplicationEventContext) || PlainDataClasses.Contains(t);

	public static FieldInfo[] Fields(Type t)
	{
		return FieldsCache.GetOrAdd(t, (Type type) =>
		{
			var fields = new List<FieldInfo>();
			Type current = type;
			while (current != null && current != typeof(object) && current != typeof(ValueType))
			{
				fields.AddRange(current.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
				current = current.BaseType;
			}
			return fields.ToArray();
		});
	}

	private static FieldInfo? Field(Type t, string name) => Fields(t).FirstOrDefault((FieldInfo f) => f.Name == name);

	public static WireValue Encode(object? o, Type declared, ObjectSpace sp, ref string? fail, int depth = 0, bool copyObservables = false)
	{
		if (fail != null)
		{
			return WireValue.NullValue;
		}
		if (o == null)
		{
			return WireValue.NullValue;
		}
		if (depth > 14)
		{
			fail = declared.Name + " nested too deeply";
			return WireValue.NullValue;
		}
		switch (o)
		{
		case Entity entity:
		{
			uint? netId = sp.IdOf(entity);
			if (netId.HasValue)
			{
				return WireValue.Ent(netId.Value);
			}
			// Not replicated (yet): hand out a negative per-message id the receiver turns into a stub.
			if (!sp.LocalIds.TryGetValue(entity, out var localId))
			{
				localId = sp.LocalIds[entity] = sp.NextLocalId();
			}
			return new WireValue
			{
				Kind = WireKind.Entity,
				Id = (uint)localId
			};
		}
		case Component component:
		{
			uint? entityId = sp.IdOf(component.Entity);
			if (!entityId.HasValue)
			{
				fail = component.GetType().Name + " on an entity the other side does not have";
				return WireValue.NullValue;
			}
			return WireValue.Comp(entityId.Value, TypeName(component.GetType()));
		}
		case Session:
			return WireValue.Of(WireKind.Session);
		case Definition definition:
			return WireValue.Def(definition.Guid);
		case string s:
			return WireValue.Str(s);
		case StringId stringId:
			return WireValue.Str(stringId.String ?? "");
		case Type t:
			return WireValue.TypeName(TypeName(t));
		case Delegate d:
			fail = "a delegate (" + d.Method.Name + ") cannot travel";
			return WireValue.NullValue;
		}
		if (o is ObservableObject && !copyObservables)
		{
			if (sp.ObservableId?.Invoke(o) is uint oid)
			{
				sp.Observed.Add(oid);
				return WireValue.Observable(oid);
			}
			// A toolbar action is a plain value (detail model type + member name) that derives from ObservableObject.
			// The control panel creates it locally (no pairing id), so it travels by value (Keen-serialized below).
			if (!(o is IToolbarAction && IsKeenSerializable(o.GetType())))
			{
				if (sp.ObservableId == null)
				{
					fail = o.GetType().Name + " is an observable object (shared by replication pairing; this side does not name models)";
					return WireValue.NullValue;
				}
				Func<object, bool>? wasReleased = sp.WasReleased;
				fail = wasReleased != null && wasReleased(o)
					? o.GetType().Name + " was already released by the host"
					: o.GetType().Name + " is an observable model the other side cannot have (not replicated)";
				return WireValue.NullValue;
			}
		}
		Type type = o.GetType();
		if (o is IList observedList && o is INotifyCollectionChanged && sp.ListId?.Invoke(observedList) is uint listId)
		{
			sp.Observed.Add(listId);
			return WireValue.List(listId);
		}
		if (type.IsArray && type.GetArrayRank() == 1)
		{
			Type elementType = type.GetElementType();
			var array = (Array)o;
			var items = new List<WireValue>(array.Length);
			foreach (object item in array)
			{
				items.Add(Encode(item, elementType, sp, ref fail, depth + 1, copyObservables));
			}
			return WireValue.Arr(TypeName(elementType), items);
		}
		if (type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(List<>) || type.GetGenericTypeDefinition() == typeof(System.Collections.ObjectModel.ObservableCollection<>)))
		{
			Type elementType = type.GetGenericArguments()[0];
			var items = new List<WireValue>();
			foreach (object item in (IList)o)
			{
				items.Add(Encode(item, elementType, sp, ref fail, depth + 1, copyObservables));
			}
			return WireValue.Arr("List:" + TypeName(elementType), items);
		}
		if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ImmutableArray<>))
		{
			Type elementType = type.GetGenericArguments()[0];
			var items = new List<WireValue>();
			foreach (object item in (IEnumerable)o)
			{
				items.Add(Encode(item, elementType, sp, ref fail, depth + 1, copyObservables));
			}
			return WireValue.Arr("ImmutableArray:" + TypeName(elementType), items);
		}
		if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
		{
			Type[] keyValueTypes = type.GetGenericArguments();
			var items = new List<WireValue>();
			foreach (DictionaryEntry entry in (IDictionary)o)
			{
				items.Add(Encode(entry.Key, keyValueTypes[0], sp, ref fail, depth + 1, copyObservables));
				items.Add(Encode(entry.Value, keyValueTypes[1], sp, ref fail, depth + 1, copyObservables));
			}
			return WireValue.Arr("Dict:" + TypeName(keyValueTypes[0]) + "|" + TypeName(keyValueTypes[1]), items);
		}
		if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(HashSet<>))
		{
			Type elementType = type.GetGenericArguments()[0];
			var items = new List<WireValue>();
			foreach (object item in (IEnumerable)o)
			{
				items.Add(Encode(item, elementType, sp, ref fail, depth + 1, copyObservables));
			}
			return WireValue.Arr("Set:" + TypeName(elementType), items);
		}
		bool declaredExactly = declared == type || Nullable.GetUnderlyingType(declared) == type || declared.IsEnum;
		if (type.IsValueType && !ContainsReferences(type) && declaredExactly)
		{
			if (ContainsStringId(type, 0))
			{
				return EncodeFields(o, type, sp, ref fail, depth, copyObservables);
			}
			byte[] bytes = ToBytes(o, type);
			string? local = sp.OpaqueHandles ? null : ProcessLocal(type, 0);
			// An all-zero value (an unassigned handle) is safe to send even if the type can hold a local one.
			if (local != null && Array.Exists(bytes, (byte b) => b != 0))
			{
				fail = type.Name + " carries a process-local " + local;
				return WireValue.NullValue;
			}
			return WireValue.Raw(bytes);
		}
		if (IsCompilerGenerated(type) || type.IsValueType || type == typeof(ReplicationEventContext))
		{
			return EncodeFields(o, type, sp, ref fail, depth, copyObservables || IsReleaseToServer(type));
		}
		if (PlainDataClasses.Contains(type))
		{
			return EncodeFields(o, type, sp, ref fail, depth, copyObservables);
		}
		if (IsKeenSerializable(type))
		{
			try
			{
				return WireValue.Ser(TypeName(type), SerializeKeen(o, type, sp));
			}
			catch (Exception ex)
			{
				fail = $"{type.Name} did not serialize ({ex.GetType().Name}: {ex.Message})";
				return WireValue.NullValue;
			}
		}
		fail = type.FullName + " cannot travel";
		return WireValue.NullValue;
	}

	private static WireValue EncodeFields(object o, Type type, ObjectSpace sp, ref string? fail, int depth, bool copyObservables)
	{
		var fields = new List<(string, WireValue)>();
		foreach (FieldInfo field in Fields(type))
		{
			if (!field.IsLiteral && !field.IsStatic)
			{
				fields.Add((field.Name, Encode(field.GetValue(o), field.FieldType, sp, ref fail, depth + 1, copyObservables)));
				if (fail != null)
				{
					fail = $"{type.Name}.{field.Name}: {fail}";
					break;
				}
			}
		}
		return WireValue.Obj(TypeName(type), fields);
	}

	internal static Definition? FindDefinition(Guid guid, Type declared)
	{
		DefinitionManager definitions = Singleton<DefinitionManager>.Instance;
		if (typeof(Configuration).IsAssignableFrom(declared) && declared != typeof(Configuration))
		{
			return definitions.TryGetConfiguration(declared, out Configuration? configuration) ? configuration : null;
		}
		Func<DefinitionManager, Guid, Definition?>? tryAny = TryAny();
		Definition? definition;
		if (tryAny != null)
		{
			definition = tryAny(definitions, guid);
		}
		else if (!definitions.TryGetDefinition(guid, out definition))
		{
			definition = null;
		}
		// For a configuration, return the instance the manager currently holds for that type.
		if (definition is Configuration found && definitions.TryGetConfiguration(found.GetType(), out Configuration? live) && live != null)
		{
			return live;
		}
		return definition;
	}

	private static Func<DefinitionManager, Guid, Definition?>? TryAny()
	{
		if (_tryAnyResolved)
		{
			return _tryAny;
		}
		try
		{
			MethodInfo method = EngineMembers.Method(HookTargets.Reflected.Single((EngineMember m) => m.Id == "defs-tryany"));
			TryAnyFn fn = method.CreateDelegate<TryAnyFn>();
			_tryAny = (DefinitionManager dm, Guid g) => !fn(dm, g, out var definition) ? null : definition;
		}
		catch (Exception)
		{
			_tryAny = null;
		}
		_tryAnyResolved = true;
		return _tryAny;
	}

	public static bool IsUnknownModel(string fail) =>
		fail.Contains("is not on this side", StringComparison.Ordinal)
		&& (fail.Contains("observable #", StringComparison.Ordinal) || fail.Contains("observed list #", StringComparison.Ordinal));

	public static object? Decode(WireValue v, Type declared, ObjectSpace sp, bool translated, ref string? fail, int depth = 0, bool inCollection = false)
	{
		if (fail != null)
		{
			return null;
		}
		if (declared.IsByRef)
		{
			declared = declared.GetElementType();
		}
		switch (v.Kind)
		{
		case WireKind.Null:
		case WireKind.Default:
			return declared.IsValueType && Nullable.GetUnderlyingType(declared) == null ? Activator.CreateInstance(declared) : null;
		case WireKind.Entity:
		{
			int id = (int)v.Id;
			// Ids below -1 are the sender's per-message local ids (see Encode): one stub per id.
			if (id < -1)
			{
				if (!sp.LocalStubs.TryGetValue(id, out Entity stub))
				{
					stub = sp.LocalStubs[id] = EntityAllocator.AllocateStub();
				}
				return stub;
			}
			Entity? entity = sp.Resolve(v.Id, translated);
			if (entity == null)
			{
				fail = $"entity #{v.Id} is not on this side{sp.Explain?.Invoke(v.Id)}";
			}
			return entity;
		}
		case WireKind.Component:
		{
			Entity? entity = sp.Resolve(v.Id, translated);
			if (entity == null)
			{
				fail = $"component's entity #{v.Id} is not on this side{sp.Explain?.Invoke(v.Id)}";
				return null;
			}
			Type? exactType = WireTypes.Resolve(v.Text, declared);
			Component? match = null;
			foreach (Component candidate in entity.Components)
			{
				if (declared.IsInstanceOfType(candidate))
				{
					if (exactType != null && candidate.GetType() == exactType)
					{
						return candidate;
					}
					match ??= candidate;
				}
			}
			if (match == null)
			{
				fail = $"no {declared.Name} on entity #{v.Id}";
			}
			return match;
		}
		case WireKind.Session:
		{
			Session? session = translated ? sp.TranslatedSession : sp.Session;
			if (session == null)
			{
				fail = translated ? "no session to translate on this side" : "no session on this side";
			}
			return session;
		}
		case WireKind.Observable:
		{
			object? observable = sp.ResolveObservable?.Invoke(v.Id, inCollection);
			if (observable == null)
			{
				fail = $"observable #{v.Id} is not on this side";
				return null;
			}
			if (!(Nullable.GetUnderlyingType(declared) ?? declared).IsInstanceOfType(observable))
			{
				fail = $"observable #{v.Id} is a {observable.GetType().Name} here, not a {declared.Name}";
				return null;
			}
			return observable;
		}
		case WireKind.ObservedList:
		{
			object? list = sp.ResolveList?.Invoke(v.Id);
			if (list == null)
			{
				fail = $"observed list #{v.Id} is not on this side";
			}
			return list;
		}
		case WireKind.Definition:
		{
			Definition? definition = FindDefinition(v.Guid, Nullable.GetUnderlyingType(declared) ?? declared);
			if (definition != null)
			{
				return definition;
			}
			fail = $"definition {v.Guid} is not loaded here";
			return null;
		}
		case WireKind.String:
			if (declared == typeof(StringId) || declared == typeof(StringId?))
			{
				return StringId.Get(v.Text);
			}
			return v.Text;
		case WireKind.Type:
		{
			Type? type = WireTypes.Resolve(v.Text, typeof(object));
			if (type == null)
			{
				fail = "type " + v.Text + " not found or not allowed";
			}
			return type;
		}
		case WireKind.Bytes:
		{
			Type type = Nullable.GetUnderlyingType(declared) ?? declared;
			if (!type.IsValueType || ContainsReferences(type))
			{
				fail = "raw bytes for " + declared.Name;
				return null;
			}
			if (CommandLayouts.SizeOf(type) != v.Bytes.Length)
			{
				fail = $"{type.Name}: {v.Bytes.Length} B, expected {CommandLayouts.SizeOf(type)} B";
				return null;
			}
			return FromBytes(v.Bytes, type);
		}
		case WireKind.Array:
		{
			if (v.Text.StartsWith("Dict:", StringComparison.Ordinal))
			{
				string[] typeNames = v.Text[DictPrefix.Length..].Split('|');
				Type? keyType = typeNames.Length == 2 ? WireTypes.Resolve(typeNames[0], typeof(object)) : null;
				Type? valueType = typeNames.Length == 2 ? WireTypes.Resolve(typeNames[1], typeof(object)) : null;
				Type? dictionaryType = keyType == null || valueType == null ? null : Collection(v.Text, typeof(Dictionary<,>).MakeGenericType(keyType, valueType), declared);
				if (dictionaryType == null || v.Items.Count % 2 != 0)
				{
					fail = "dictionary " + v.Text + " not readable here";
					return null;
				}
				var dictionary = (IDictionary)Activator.CreateInstance(dictionaryType);
				for (int i = 0; i + 1 < v.Items.Count; i += 2)
				{
					object? key = Decode(v.Items[i], keyType, sp, translated: false, ref fail, depth + 1, inCollection: true);
					object? value = Decode(v.Items[i + 1], valueType, sp, translated: false, ref fail, depth + 1, inCollection: true);
					if (fail != null)
					{
						return null;
					}
					if (key != null)
					{
						dictionary[key] = value;
					}
				}
				return dictionary;
			}
			if (v.Text.StartsWith("Set:", StringComparison.Ordinal))
			{
				Type? elementType = WireTypes.Resolve(v.Text[SetPrefix.Length..], typeof(object));
				Type? setType = elementType == null ? null : Collection(v.Text, typeof(HashSet<>).MakeGenericType(elementType), declared);
				if (setType == null)
				{
					fail = "set element type " + v.Text + " not found or not allowed";
					return null;
				}
				object set = Activator.CreateInstance(setType);
				MethodInfo add = set.GetType().GetMethod("Add");
				foreach (WireValue item in v.Items)
				{
					object? element = Decode(item, elementType, sp, translated: false, ref fail, depth + 1, inCollection: true);
					if (fail != null)
					{
						return null;
					}
					add.Invoke(set, new object[] { element });
				}
				return set;
			}
			string text = v.Text;
			bool isList = text.StartsWith("List:", StringComparison.Ordinal);
			bool isImmutable = text.StartsWith("ImmutableArray:", StringComparison.Ordinal);
			string elementName = isList ? text[5..] : isImmutable ? text[15..] : text;
			Type? itemType = WireTypes.Resolve(elementName, typeof(object));
			// A plain (unreplicated) ObservableCollection travels as a list and is rebuilt as the slot's own type.
			Type? listType = itemType == null ? null : (Nullable.GetUnderlyingType(declared) ?? declared) == typeof(System.Collections.ObjectModel.ObservableCollection<>).MakeGenericType(itemType)
				? typeof(System.Collections.ObjectModel.ObservableCollection<>).MakeGenericType(itemType) : typeof(List<>).MakeGenericType(itemType);
			if (itemType == null || Collection(text, isList ? listType : isImmutable ? typeof(ImmutableArray<>).MakeGenericType(itemType) : itemType.MakeArrayType(), declared) == null)
			{
				fail = "element type " + text + " not found or not allowed";
				return null;
			}
			Array array = Array.CreateInstance(itemType, v.Items.Count);
			for (int i = 0; i < v.Items.Count; i++)
			{
				array.SetValue(Decode(v.Items[i], itemType, sp, translated: false, ref fail, depth + 1, inCollection: true), i);
			}
			if (isList)
			{
				var list = (IList)Activator.CreateInstance(listType);
				foreach (object item in array)
				{
					list.Add(item);
				}
				return list;
			}
			if (isImmutable)
			{
				return typeof(ImmutableArray).GetMethods()
					.First((MethodInfo m) => m.Name == "Create" && m.GetParameters() is { Length: 1 } parameters && parameters[0].ParameterType.IsArray)
					.MakeGenericMethod(itemType)
					.Invoke(null, new object[] { array });
			}
			return array;
		}
		case WireKind.Object:
		{
			Type? type = WireTypes.Resolve(v.Text, declared, construct: true);
			if (type != null && !IsFieldCopied(type))
			{
				WireTypes.Refuse(v.Text, declared, "not a type Encode copies field by field");
				type = null;
			}
			if (type == null)
			{
				fail = "type " + v.Text + " not found or not allowed";
				return null;
			}
			object instance = type.IsValueType ? Activator.CreateInstance(type) : RuntimeHelpers.GetUninitializedObject(type);
			foreach (var (name, value) in v.Fields)
			{
				FieldInfo? field = Field(type, name);
				if (field != null && WireTypes.Problem(field.DeclaringType) != null)
				{
					WireTypes.Refuse(v.Text, declared, "field " + name + " is declared on " + field.DeclaringType.FullName);
					field = null;
				}
				if (field == null)
				{
					fail = type.Name + " has no field " + name + " here";
					return null;
				}
				object? decoded = Decode(value, field.FieldType, sp, translated: false, ref fail, depth + 1, inCollection);
				if (fail != null)
				{
					fail = $"{type.Name}.{name}: {fail}";
					return null;
				}
				field.SetValue(instance, decoded);
			}
			return instance;
		}
		case WireKind.Serialized:
		{
			Type? type = WireTypes.Resolve(v.Text, declared, construct: true);
			if (type != null && !IsKeenSerializable(type))
			{
				WireTypes.Refuse(v.Text, declared, "not Keen-serializable");
				type = null;
			}
			if (type == null)
			{
				fail = "type " + v.Text + " not found or not allowed";
				return null;
			}
			try
			{
				return DeserializeKeen(v.Bytes, type, sp);
			}
			catch (Exception ex)
			{
				fail = $"{type.Name} did not deserialize ({ex.GetType().Name}: {ex.Message})";
				return null;
			}
		}
		default:
			fail = $"{v.Kind} cannot be decoded as {declared.Name}";
			return null;
		}
	}

	private static Type? Collection(string name, Type collection, Type declared)
	{
		Type slot = Nullable.GetUnderlyingType(declared) ?? declared;
		if (slot.ContainsGenericParameters || slot.IsAssignableFrom(collection))
		{
			return collection;
		}
		// A signal's ImmutableArray argument is a command-buffer reference to its backing array (CommandLayouts Ref segment).
		if (collection.IsArray && slot.IsGenericType && slot.GetGenericTypeDefinition() == typeof(ImmutableArray<>) && slot.GetGenericArguments()[0] == collection.GetElementType())
		{
			return collection;
		}
		WireTypes.Refuse(name, declared, "a " + collection.Name + " does not fit the slot");
		return null;
	}

	public static bool ContainsReferences(Type t) => RefCache.GetOrAdd(t, (Type x) => (bool)IsRefMethod.MakeGenericMethod(x).Invoke(null, null));

	public static bool IsReleaseToServer(Type t) => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(ReleaseToServer<>);

	public static string? TypeProblem(Type t, bool copyObservables = false, int depth = 0)
	{
		if (t.IsByRef)
		{
			t = t.GetElementType();
		}
		t = Nullable.GetUnderlyingType(t) ?? t;
		if (depth > 6)
		{
			return null;
		}
		if (t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(StringId) || t == typeof(Type) || t == typeof(decimal) || t == typeof(Guid))
		{
			return null;
		}
		if (typeof(Entity).IsAssignableFrom(t) || typeof(Component).IsAssignableFrom(t) || typeof(Session).IsAssignableFrom(t) || typeof(Definition).IsAssignableFrom(t))
		{
			return null;
		}
		if (typeof(Delegate).IsAssignableFrom(t))
		{
			return "delegate";
		}
		if (typeof(ObservableObject).IsAssignableFrom(t))
		{
			return null;
		}
		if (t.IsArray)
		{
			return TypeProblem(t.GetElementType(), copyObservables, depth + 1);
		}
		if (t.IsGenericType && (t.GetGenericTypeDefinition() == typeof(List<>) || t.GetGenericTypeDefinition() == typeof(ImmutableArray<>) || t.GetGenericTypeDefinition() == typeof(HashSet<>) || t.GetGenericTypeDefinition() == typeof(System.Collections.ObjectModel.ObservableCollection<>)))
		{
			return TypeProblem(t.GetGenericArguments()[0], copyObservables, depth + 1);
		}
		if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Dictionary<,>))
		{
			return TypeProblem(t.GetGenericArguments()[0], copyObservables, depth + 1) ?? TypeProblem(t.GetGenericArguments()[1], copyObservables, depth + 1);
		}
		if (t.IsValueType && !ContainsReferences(t))
		{
			string? local = ContainsStringId(t, 0) ? null : ProcessLocal(t, 0);
			if (local != null)
			{
				return t.Name + " (" + local + ": only while unassigned)";
			}
			return null;
		}
		if (IsCompilerGenerated(t) || t.IsValueType || t == typeof(ReplicationEventContext))
		{
			bool fieldCopyObservables = copyObservables || IsReleaseToServer(t);
			foreach (FieldInfo field in Fields(t))
			{
				if (!field.IsStatic && !field.IsLiteral)
				{
					string? problem = TypeProblem(field.FieldType, fieldCopyObservables, depth + 1);
					if (problem != null)
					{
						return $"{t.Name}.{field.Name}: {problem}";
					}
				}
			}
			return null;
		}
		if (IsKeenSerializable(t))
		{
			return null;
		}
		if (t == typeof(object) || t.IsInterface || t.IsAbstract)
		{
			return t.Name + " (depends on the value)";
		}
		return t.Name + " cannot travel";
	}

	// Closures and other compiler-made types, including types nested in them (display classes).
	public static bool IsCompilerGenerated(Type t)
	{
		if (!t.Name.Contains('<') && t.GetCustomAttribute<CompilerGeneratedAttribute>() == null)
		{
			if (t.DeclaringType != null && IsCompilerGenerated(t.DeclaringType))
			{
				return t.Name.Contains("DisplayClass");
			}
			return false;
		}
		return true;
	}

	private static bool ContainsStringId(Type t, int depth)
	{
		if (t == typeof(StringId))
		{
			return true;
		}
		if (t.IsPrimitive || t.IsEnum || depth > 4)
		{
			return false;
		}
		foreach (FieldInfo field in Fields(t))
		{
			if (!field.IsStatic && ContainsStringId(field.FieldType, depth + 1))
			{
				return true;
			}
		}
		return false;
	}

	public static string? ProcessLocal(Type t, int depth)
	{
		if (t.IsPointer || t == typeof(nint) || t == typeof(nuint))
		{
			return "pointer";
		}
		if (t == typeof(EntityId))
		{
			return null;
		}
		if (t.FullName == "Keen.VRage.DCS.Accessors.DEntity" || t.Name == "EntityId")
		{
			return t.Name;
		}
		if (t.IsPrimitive || t.IsEnum || depth > 4)
		{
			return null;
		}
		foreach (FieldInfo field in Fields(t))
		{
			if (!field.IsStatic)
			{
				string? found = ProcessLocal(field.FieldType, depth + 1);
				if (found != null)
				{
					return found;
				}
			}
		}
		return null;
	}

	private static bool IsKeenSerializable(Type t) => SerializableCache.GetOrAdd(t, (Type x) => typeof(ISerializable).IsAssignableFrom(x));

	public static byte[] ToBytes(object box, Type t)
	{
		return ToBytesCache.GetOrAdd(t, (Type x) => (Func<object, byte[]>)typeof(ObjectCodec).GetMethod("ToBytesT", BindingFlags.Static | BindingFlags.NonPublic).MakeGenericMethod(x).CreateDelegate(typeof(Func<object, byte[]>)))(box);
	}

	public static object FromBytes(byte[] b, Type t)
	{
		return FromBytesCache.GetOrAdd(t, (Type x) => (Func<byte[], object>)typeof(ObjectCodec).GetMethod("FromBytesT", BindingFlags.Static | BindingFlags.NonPublic).MakeGenericMethod(x).CreateDelegate(typeof(Func<byte[], object>)))(b);
	}

	private static byte[] ToBytesT<T>(object box) where T : struct
	{
		T value = (T)box;
		return MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref value, 1)).ToArray();
	}

	private static object FromBytesT<T>(byte[] b) where T : struct => MemoryMarshal.Read<T>(b);

	private static byte[] SerializeKeen(object o, Type t, ObjectSpace sp)
	{
		bool bundle = typeof(EntityBundle).IsAssignableFrom(t);
		var proxies = new NetIdProxyContext((Entity e) =>
		{
			uint? netId = sp.IdOf(e);
			if (netId.HasValue)
			{
				return (int)netId.Value;
			}
			if (sp.LocalIds.TryGetValue(e, out var localId) || sp.BundleLocalIds.TryGetValue(e, out localId))
			{
				return localId;
			}
			int newId = sp.NextLocalId();
			if (bundle)
			{
				sp.BundleLocalIds[e] = newId;
			}
			else
			{
				sp.LocalIds[e] = newId;
			}
			return newId;
		}, null);
		return EngineCodec.SerializeObject(o, t, proxies, SerializeGeneric);
	}

	private static object? DeserializeKeen(byte[] b, Type t, ObjectSpace sp)
	{
		var proxies = new NetIdProxyContext(null, (int id) =>
		{
			if (id < -1)
			{
				if (!sp.LocalStubs.TryGetValue(id, out Entity stub))
				{
					stub = sp.LocalStubs[id] = EntityAllocator.AllocateStub();
				}
				return stub;
			}
			return sp.Resolve((uint)id, false) ?? EntityAllocator.AllocateStub();
		});
		return EngineCodec.DeserializeObject(b, t, proxies, DeserializeGeneric);
	}
}
