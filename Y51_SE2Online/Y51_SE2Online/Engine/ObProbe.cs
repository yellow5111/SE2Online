#if !PULSAR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Keen.Game2.Simulation.WorldObjects.Characters;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Mathematics;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.Engine;

/// <summary>
/// Test rig only (-y51online:obprobe[:metres]): every 5 s each game serializes every character, grid and block within
/// 60 m (or the given range) of a player the way a save does and logs each component's object builder, flattened, when it
/// changed since it was last logged and all of them once a minute: "stateprobe S|C ms ob:Type netId value" lines for
/// propcompare.py. Whatever a component saves is compared between the server and each player, so a value a joining
/// player gets wrong shows up without a probe of its own. Positions, rotations and velocities are left out (motion has
/// its own probes).
/// </summary>
internal static class ObProbe
{
	private static readonly string? Arg = MpPlugin.LaunchArgs.FirstOrDefault(a => a.StartsWith(MpOptions.Prefix + "obprobe", StringComparison.Ordinal));
	public static bool Requested => Arg != null;
	private static readonly double Range = Arg != null && Arg.Split(':').Length > 2 && double.TryParse(Arg.Split(':')[2], out var m) ? m : 60;
	private const int MaxValue = 3000;

	private static long _serverNextMs, _clientNextMs, _serverFullMs, _clientFullMs;
	private static readonly Dictionary<(bool Server, ulong Id, string Name), string> Logged = new();
	private static readonly Dictionary<Type, MemberInfo[]> Members = new();
	private static readonly HashSet<string> Failed = new();
	private static Func<Entity, ulong?> _idOf = _ => null;

	public static void Log(Session session, bool server, List<Vector3D> around, Func<Entity, ulong?> idOf)
	{
		if (!Requested || around.Count == 0) return;
		_idOf = idOf;
		long now = MpClock.NowMs();
		ref long next = ref server ? ref _serverNextMs : ref _clientNextMs;
		if (now < next) return;
		next = now + 5000;
		ref long full = ref server ? ref _serverFullMs : ref _clientFullMs;
		bool all = now >= full;
		if (all) full = now + 60_000;
		var sb = new StringBuilder();
		string side = server ? "S" : "C";
		bool Near(Entity e)
		{
			var root = e.GetTopLevelParent();
			return root.Data.TryGet<WorldTransform>(out var t) && around.Any(a => Vector3D.Distance(t.Position, a) <= Range);
		}
		void Log(Entity e)
		{
			if (idOf(e) is not ulong id || e.Scene is not { } scene) return;
			Keen.VRage.DCS.ObjectBuilders.EntityObjectBuilder eob;
			try { eob = scene.EntitySerializer.Serialize(e); }
			catch (Exception ex)
			{
				if (Failed.Add(e.DebugName ?? "?")) GameLog.Instance.Info($"selftest: obprobe: {e.DebugName} does not serialize here ({ex.GetType().Name}: {ex.Message})");
				return;
			}
			foreach (var ob in eob.ObjectBuilders.AsSpan())
			{
				if (ob == null) continue;
				string name = "ob:" + ob.GetType().Name;
				var text = new StringBuilder();
				try { Dump(ob, text, 0); }
				catch (Exception ex) { text.Append('!').Append(ex.GetType().Name); }
				string value = text.Length > MaxValue ? text.ToString(0, MaxValue) + "..." : text.ToString();
				if (value.Length == 0) continue;
				var key = (server, id, name);
				if (!all && Logged.TryGetValue(key, out var last) && last == value) continue;
				Logged[key] = value;
				sb.Append($"stateprobe {side} {now} {name} {id} {value}\n");
			}
		}
		foreach (Entity c in session.GetEntitiesOfType<CharacterLifeComponent>()) if (Near(c)) Log(c);
		foreach (Entity grid in session.GetEntitiesOfType<CubeGridComponent>())
		{
			if (!Near(grid)) continue;
			Log(grid);
			grid.Get<CubeGridComponent>().VisitAllBlocksWithComponent((CubeBlockComponent b) => { if (Near(b.Entity)) Log(b.Entity); });
		}
		if (sb.Length > 0) GameLog.Instance.Info(sb.ToString().TrimEnd('\n'));
	}

	private static MemberInfo[] MembersOf(Type type)
	{
		if (Members.TryGetValue(type, out var known)) return known;
		var list = new List<MemberInfo>();
		for (var t = type; t != null && t != typeof(object); t = t.BaseType)
		{
			foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)) list.Add(f);
			foreach (var p in t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
				if (p.GetMethod != null && p.GetIndexParameters().Length == 0 && p.CanWrite) list.Add(p);
		}
		return Members[type] = list.OrderBy(m => m.Name, StringComparer.Ordinal).ToArray();
	}

	private static bool Motion(Type type)
	{
		string n = type.Name;
		return n.StartsWith("Vector", StringComparison.Ordinal) || n.StartsWith("Quaternion", StringComparison.Ordinal) || n.StartsWith("Matrix", StringComparison.Ordinal)
			|| n.Contains("Transform", StringComparison.Ordinal) || n.Contains("Velocity", StringComparison.Ordinal);
	}

	private static void Dump(object? value, StringBuilder sb, int depth)
	{
		switch (value)
		{
		case null: sb.Append('-'); return;
		case string s: sb.Append(s.Replace(' ', '_').Replace('\n', '|')); return;
		case float f: sb.Append(f.ToString("0.##")); return;
		case double d: sb.Append(d.ToString("0.##")); return;
		case bool b: sb.Append(b ? '1' : '0'); return;
		case Entity entity: sb.Append(_idOf(entity) is ulong netId ? "#" + netId : "#?"); return;
		case Enum or Guid or decimal or TimeSpan or DateTime: sb.Append(value); return;
		// Interned per process: the text, not the id.
		case Keen.VRage.Library.Utils.StringId id: sb.Append(id.String); return;
		}
		var type = value.GetType();
		// Children are compared as entities of their own; their builders name server and client compositions.
		if (value is Keen.VRage.DCS.ObjectBuilders.EntityObjectBuilder) { sb.Append("eob"); return; }
		if (type.IsPrimitive) { sb.Append(value); return; }
		if (Motion(type)) { sb.Append('~'); return; }
		if (depth >= 4 || type.Namespace?.Contains("Definitions", StringComparison.Ordinal) == true || type.Name.EndsWith("Definition", StringComparison.Ordinal))
		{
			sb.Append(value.ToString()?.Split('\\', '/').Last().Replace(' ', '_'));
			return;
		}
		if (value is IEnumerable items)
		{
			sb.Append('[');
			int n = 0;
			foreach (var item in items)
			{
				if (n++ > 0) sb.Append(',');
				if (n > 64) { sb.Append("..."); break; }
				Dump(item, sb, depth + 1);
			}
			sb.Append(']');
			return;
		}
		sb.Append('{');
		bool first = true;
		foreach (var member in MembersOf(type))
		{
			Type memberType = member is FieldInfo fi ? fi.FieldType : ((PropertyInfo)member).PropertyType;
			if (Motion(memberType)) continue;
			object? v;
			try { v = member is FieldInfo field ? field.GetValue(value) : ((PropertyInfo)member).GetValue(value); }
			catch { continue; }
			if (!first) sb.Append(';');
			first = false;
			sb.Append(member.Name).Append('=');
			Dump(v, sb, depth + 1);
		}
		sb.Append('}');
	}
}
#endif
