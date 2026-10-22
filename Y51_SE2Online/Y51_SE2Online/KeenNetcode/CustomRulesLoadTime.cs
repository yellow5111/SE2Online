using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Keen.VRage.Library.Definitions;
using Keen.VRage.Library.Serialization;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// A few definition types work values out once, in their typed Init(builder): InventoryDefinition sorts its whitelist,
/// CharacterPhysicsComponentDefinition turns slope angles into cosines, encounter spawners build samplers. The pattern: Init
/// sets a member the object builder does not have. Writing a rule into the stored member alone would leave the worked-out
/// value at the game's, so such a definition is initialised again from a builder rebuilt out of its own (overridden)
/// members, which works them out anew. That needs every builder field to have a member of the same name on the
/// definition; types with builder-only fields (a few voxel/planet types) are reported as "partly" instead.
/// </summary>
internal static class CustomRulesLoadTime
{
	public sealed record Info(bool Computed, bool Recomputable, string[] Derived, string[] BuilderOnly, MethodInfo Init, Type Builder);

	private static readonly ConcurrentDictionary<Type, Info> Cache = new();
	private static readonly HashSet<string> Logged = new();
	private static readonly PropertyInfo DebugNameInternal = typeof(DefinitionObjectBuilder).GetProperty("DebugNameInternal", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
	private static readonly HashSet<string> Ignored = new(StringComparer.Ordinal) { "Guid", "DebugNameInternal", "DebugName" };

	public static Info For(Type type) => Cache.GetOrAdd(type, Scan);

	/// <summary>For the editors: what this type works out at load, and whether custom rules refresh it. Null for most types.</summary>
	public static string Describe(Type type)
	{
		var info = For(type);
		if (!info.Computed) return null;
		string derived = string.Join(", ", info.Derived.Select(Core.RuleText.Label));
		return info.Recomputable ? $"works out {derived} when the game loads; custom rules refresh it"
			: $"works out {derived} at load from {string.Join(", ", info.BuilderOnly.Select(Core.RuleText.Label))}: changes to those keep the game's worked-out values";
	}

	private static Info Scan(Type type)
	{
		var none = new Info(false, false, Array.Empty<string>(), Array.Empty<string>(), null, null);
		try
		{
			var init = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
				.FirstOrDefault(m => m.Name == "Init" && m.GetParameters().Length == 1 && typeof(DefinitionObjectBuilder).IsAssignableFrom(m.GetParameters()[0].ParameterType));
			if (init == null) return none;
			var builder = init.GetParameters()[0].ParameterType;
			var builderFields = builder.GetFields(BindingFlags.Instance | BindingFlags.Public).Where(f => !f.IsInitOnly).ToList();
			var names = new HashSet<string>(builderFields.Select(f => f.Name), StringComparer.Ordinal);
			var derived = new HashSet<string>(StringComparer.Ordinal);
			byte[] il = init.GetMethodBody()?.GetILAsByteArray() ?? Array.Empty<byte>();
			// call, callvirt, newobj, stfld with a 4-byte token: enough to see which members Init sets beyond the builder's copies.
			for (int i = 0; i + 4 < il.Length; i++)
			{
				if (il[i] is not (0x28 or 0x6F or 0x7D)) continue;
				int token = BitConverter.ToInt32(il, i + 1);
				try
				{
					// Only members of the definition itself: setters on child objects (list items, nested records) are not its values.
					string name = il[i] == 0x7D && token >> 24 == 0x04 ? init.Module.ResolveField(token) is { } f && f.DeclaringType.IsAssignableFrom(type) ? f.Name : null
						: token >> 24 is 0x06 or 0x0A ? init.Module.ResolveMethod(token) is { Name: var n } m && n.StartsWith("set_", StringComparison.Ordinal) && m.DeclaringType.IsAssignableFrom(type) ? n.Substring(4) : null
						: null;
					if (name != null && !names.Contains(name) && !Ignored.Contains(name) && !name.StartsWith("<", StringComparison.Ordinal)) derived.Add(name);
					if (name != null) i += 4;
				}
				catch (Exception) { }
			}
			if (derived.Count == 0) return none;
			var builderOnly = builderFields.Where(f => !Ignored.Contains(f.Name) && Member(type, f.Name) == null).Select(f => f.Name).ToArray();
			return new Info(true, builderOnly.Length == 0, derived.ToArray(), builderOnly, init, builder);
		}
		catch (Exception) { return none; }
	}

	private static MemberInfo Member(Type type, string name)
		=> (MemberInfo)type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
			?? type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

	private static object Value(MemberInfo member, object target) => member is PropertyInfo p ? p.GetValue(target) : ((FieldInfo)member).GetValue(target);

	// Init copied the builder's field into the definition, sometimes through a wrapper (HashSet into a HashSetReader, a
	// list into an immutable array): undo that without the serializer, which cannot read definition references here.
	private static object Convert(object value, Type target, Type memberType)
	{
		if (value == null || target.IsInstanceOfType(value)) return value;
		foreach (var inner in value.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
			if (target.IsAssignableFrom(inner.FieldType) && inner.GetValue(value) is { } wrapped) return wrapped;
		foreach (var owner in new[] { target, value.GetType() })
			foreach (var op in owner.GetMethods(BindingFlags.Static | BindingFlags.Public).Where(m => m.Name is "op_Implicit" or "op_Explicit" && m.ReturnType == target))
				if (op.GetParameters()[0].ParameterType.IsInstanceOfType(value)) return op.Invoke(null, new[] { value });
		if (target.IsArray && value is System.Collections.IEnumerable items)
		{
			var list = items.Cast<object>().ToList();
			var array = Array.CreateInstance(target.GetElementType()!, list.Count);
			for (int i = 0; i < list.Count; i++) array.SetValue(list[i], i);
			return array;
		}
		foreach (var ctor in target.GetConstructors())
			if (ctor.GetParameters() is { Length: 1 } ps && ps[0].ParameterType.IsInstanceOfType(value)) return ctor.Invoke(new[] { value });
		return CustomRulesEngine.FromJson(target, CustomRulesEngine.ToJson(memberType, value));
	}

	/// <summary>Initialises the definition again from its own members, so values worked out at load follow the rules. Null when done.</summary>
	public static string Recompute(Definition definition)
	{
		var info = For(definition.GetType());
		if (!info.Computed) return null;
		if (!info.Recomputable) return $"{definition.GetType().Name} works out {string.Join(", ", info.Derived)} at load from {string.Join(", ", info.BuilderOnly)}; those keep the game's values";
		try
		{
			object builder = Activator.CreateInstance(info.Builder);
			((DefinitionObjectBuilder)builder).Guid = definition.Guid;
			DebugNameInternal?.SetValue(builder, definition.DebugName);
			foreach (var field in info.Builder.GetFields(BindingFlags.Instance | BindingFlags.Public).Where(f => !f.IsInitOnly && !Ignored.Contains(f.Name)))
			{
				var member = Member(definition.GetType(), field.Name);
				field.SetValue(builder, Convert(Value(member, definition), field.FieldType, member is PropertyInfo p ? p.PropertyType : ((FieldInfo)member).FieldType));
			}
			info.Init.Invoke(definition, new[] { builder });
			if (MpPlugin.LaunchArgs.Contains(Core.MpOptions.Prefix + "selftest"))
			{
				string values = string.Join(", ", info.Derived.Select(d => d + "=" + (Member(definition.GetType(), d) is { } m ? Value(m, definition) switch
				{
					System.Collections.IEnumerable list and not string => list.Cast<object>().Count() + " entries",
					var v => v?.ToString()
				} : "?")));
				lock (Logged)
					if (Logged.Add(definition.GetType().Name + values))
						GameLog.Instance.Info($"selftest: custom rules refreshed {definition.GetType().Name} ({definition.DebugName}): {values}");
			}
			return null;
		}
		catch (Exception ex)
		{
			return $"{definition.GetType().Name}: values worked out at load could not be refreshed ({ex.GetBaseException().Message})";
		}
	}
}
