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
using Keen.VRage.DCS.Annotations.Signals;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Mathematics;
using Y51_SE2Online.Core;
using Y51_SE2Online.SyncEngine;

namespace Y51_SE2Online.Engine;

/// <summary>
/// Test rig only (-y51online:propprobe): every 5 s each game logs the value of every sync property (a component property
/// whose setter raises a SyncPropertyAttribute signal) on every character, grid and block within 150 m of a player that
/// changed since it was last logged, and all of them once a minute: "stateprobe S|C ms sp:Component.Property netId value"
/// lines for propcompare.py (which reads them as step functions).
/// </summary>
internal static class PropProbe
{
	public static bool Requested => MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "propprobe");
	private const double Range = 150;
	private static long _serverNextMs, _clientNextMs, _serverFullMs, _clientFullMs;
	private static readonly Dictionary<(bool Server, ulong Id, string Name), string> Logged = new();
	private static readonly Dictionary<Type, (string Name, PropertyInfo Property)[]> Props = new();

	private static (string, PropertyInfo)[] PropsOf(Type type)
	{
		if (Props.TryGetValue(type, out var known)) return known;
		var list = new List<(string, PropertyInfo)>();
		for (var t = type; t != null && t != typeof(object); t = t.BaseType)
			foreach (var p in t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
				if (p.GetMethod != null && p.SetMethod?.GetCustomAttribute<SignalTriggerAttribute>() is { } trigger && typeof(SyncPropertyAttribute).IsAssignableFrom(trigger.Type))
					list.Add(($"sp:{trigger.Type.DeclaringType?.Name}.{trigger.Type.Name}", p));
		return Props[type] = list.ToArray();
	}

	private static Func<Entity, ulong?> _idOf = _ => null;

	private static string Format(object? value)
	{
		switch (value)
		{
		case null: return "-";
		case Entity entity: return _idOf(entity) is ulong netId ? "#" + netId : "#?";
		case string s: return s.Replace(' ', '_');
		case float f: return f.ToString("0.###");
		case double d: return d.ToString("0.###");
		case IEnumerable items:
			var parts = new List<string>();
			foreach (var item in items) { if (parts.Count >= 8) break; parts.Add(Format(item)); }
			return "[" + string.Join(",", parts) + "]";
		default: return value.ToString()!.Replace(' ', '_');
		}
	}

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
			if (idOf(e) is not ulong id) return;
			foreach (var component in e.Components)
			{
				if (component == null) continue;
				foreach (var (name, property) in PropsOf(component.GetType()))
				{
					string value;
					try { value = Format(property.GetValue(component)); }
					catch (Exception ex) { value = "!" + ex.GetType().Name; }
					var key = (server, id, name);
					if (!all && Logged.TryGetValue(key, out var last) && last == value) continue;
					Logged[key] = value;
					sb.Append($"stateprobe {side} {now} {name} {id} {value}\n");
				}
			}
		}
		foreach (Entity c in session.GetEntitiesOfType<CharacterLifeComponent>()) if (Near(c)) Log(c);
		foreach (Entity grid in session.GetEntitiesOfType<CubeGridComponent>())
		{
			if (!Near(grid)) continue;
			Log(grid);
			grid.Get<CubeGridComponent>().VisitAllBlocksWithComponent((CubeBlockComponent b) => Log(b.Entity));
		}
		if (sb.Length > 0) GameLog.Instance.Info(sb.ToString().TrimEnd('\n'));
	}
}
#endif
