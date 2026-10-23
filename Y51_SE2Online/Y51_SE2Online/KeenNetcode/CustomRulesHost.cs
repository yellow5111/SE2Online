using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using HarmonyLib;
using Keen.Game2;
using Keen.Game2.Game.SessionComponents;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.Library.Serialization;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// Custom rules through a world's life. A world loading as a server (dedicated, co-op host or single player) gets its rules:
/// definitions when the world starts, session settings once the save is read. Players joining get the exact definition
/// values before the game asks the host for the world. A world that does not finish loading with its rules is loaded
/// without them next time, and the host is told which overrides to look at.
/// </summary>
internal static class CustomRulesHost
{
	private static bool _installed;
	private static readonly object Gate = new();
	private static RulesDocument _current;
	private static string _markerPath;
	private static string _worldLabel;
	private static List<AppliedRule> _received;
	// The join the received rules belong to (SE2 Online's connection), and whether some of them still wait for the world's
	// definitions: SE2 Online's own join gets the rules before the game loads the world (and the server's mods).
	private static object _receivedFor;
	private static int _deferred;
	// The received rules came with a join (before the server's accept): once the world's definitions are in, every value
	// must fit, or the player's world would run out of step with the server.
	private static bool _joinCheck;
	private static string _refusal;
	private static object _refusalFor;

	/// <summary>Why this game stopped loading the server's world over its custom rules (null: it did not).</summary>
	public static string Refusal { get { lock (Gate) return _refusal != null && ReferenceEquals(_refusalFor, NetHost.NetSession.Current?.Client) ? _refusal : null; } }
	private static List<AppliedRule> _hostApplied = new();
	private static bool _unsaved;
	private static long _nextSaveCheckMs;

	/// <summary>The loaded world is new (no save yet): its rules are the new-world rules until its first save.</summary>
	public static bool Unsaved { get { lock (Gate) return _unsaved; } }
	private static readonly ConditionalWeakTable<object, object> SessionDone = new();
	private static readonly Dictionary<string, Dictionary<string, object>> SessionSchema = new(StringComparer.Ordinal);

	/// <summary>Where a server's rules come from: the dedicated server.json, or a co-op world's rules file (by save id).</summary>
	public static Func<string, (RulesDocument Rules, string MarkerPath)> Source = WorldRulesStore.ForWorld;

	/// <summary>The last load's report for the host (what was applied, clamped or refused); shown on the Rules page.</summary>
	public static string LastReport { get; private set; } = "";

	/// <summary>The rules in force on the loaded server world (null: none, or this game joined someone else's).</summary>
	public static RulesDocument Current { get { lock (Gate) return _current; } }

	public static string WorldLabel { get { lock (Gate) return _worldLabel; } }

	public static void Install(Harmony harmony)
	{
		if (_installed) return;
		_installed = true;
		harmony.Patch(AccessTools.Method(typeof(WorldSessionComponent), "InitAsync"), prefix: new HarmonyMethod(typeof(CustomRulesHost), nameof(WorldInitPrefix)));
		harmony.Patch(AccessTools.Method(typeof(WorldSessionComponent), "InjectDependencies"), prefix: new HarmonyMethod(typeof(CustomRulesHost), nameof(InjectPrefix)));
		harmony.Patch(AccessTools.Method(typeof(GameAppComponent), "NotifySessionLoadFinished"), prefix: new HarmonyMethod(typeof(CustomRulesHost), nameof(LoadFinishedPrefix)));
		harmony.Patch(AccessTools.Method(typeof(GameAppComponent), nameof(GameAppComponent.LoadNetworkClientAsync)), prefix: new HarmonyMethod(typeof(CustomRulesHost), nameof(JoiningPrefix)));
		ClientRules.Apply = ApplyReceived;
		try
		{
			if (Keen.VRage.Library.Utils.Singleton<Keen.VRage.Library.Definitions.DefinitionManager>.Instance is { } definitions) definitions.DefinitionSetPushed += OnDefinitionSetPushed;
			else GameLog.Instance.Warn("custom rules: no definition manager yet; a joining player's rules are applied when its world's session builds");
		}
		catch (Exception ex) { GameLog.Instance.Warn("custom rules: " + ex.Message); }
	}

	// The world's definitions (with the server's mods) were just pushed, before anything of the world is built from them:
	// the values that waited apply now. Problems are only logged here (the push is done); InjectPrefix refuses the load.
	private static void OnDefinitionSetPushed(Keen.VRage.Library.Definitions.IDefinitionSet set)
	{
		try
		{
			if (set?.Name != "World") return;
			OwnStatePrediction.ForgetDefinitions();
			lock (Gate)
			{
				if (_received == null || !_joinCheck || !ReferenceEquals(_receivedFor, NetHost.NetSession.Current?.Client)) return;
				var problems = CustomRulesEngine.ApplyExact(_received, revertFirst: false);
				if (problems.Count == 0) GameLog.Instance.Info($"custom rules: the world's definitions are in; the server's {_received.Count} value(s) are applied: " + CustomRulesEngine.Probe(_received));
				else foreach (var problem in problems.Take(20)) GameLog.Instance.Warn("custom rules: " + problem);
			}
		}
		catch (Exception ex) { GameLog.Instance.Error("custom rules: applying the server's values to the world's definitions failed: " + ex); }
	}

	private static void WorldInitPrefix(WorldSessionComponentObjectBuilder ob)
	{
		if (!ob.IsServer) return;
		try { BeginServerWorld(ob.WorldContainerToLoad?.WorldContainerToLoad?.ToString() ?? ""); }
		catch (Exception ex) { GameLog.Instance.Error("custom rules: could not apply this world's rules: " + ex); }
	}

	private static void BeginServerWorld(string worldId)
	{
		var (rules, marker) = Source(worldId);
		string report;
		lock (Gate)
		{
			_received = null;
			_receivedFor = null;
			_joinCheck = false;
			_current = null;
			_markerPath = null;
			_worldLabel = worldId;
			_unsaved = Source == WorldRulesStore.ForWorld && WorldRulesStore.IsNew(worldId);
			SessionSchema.Clear();
			HostRules.Offer = null;
			if (rules == null || rules.IsEmpty)
			{
				int restored = CustomRulesEngine.Revert();
				LastReport = rules == null ? "" : "No custom rules for this world.";
				if (restored > 0) GameLog.Instance.Info($"custom rules: put {restored} changed definition value(s) back to the game's own for this world");
				return;
			}
			string hash = rules.Hash();
			if (LoadCheck.Read(marker) is { } failed && failed.Hash == hash)
			{
				CustomRulesEngine.Revert();
				failed.Skipped = true;
				LoadCheck.Write(marker, failed);
				LastReport = $"Custom rules are OFF for this load: the world did not finish loading with them last time{(string.IsNullOrEmpty(failed.Error) ? " (the game stopped while loading)" : ": " + failed.Error)}. "
					+ $"Look at: {Suspects(rules, failed.Error)}. Change or remove those overrides and the rules are used again.";
				GameLog.Instance.Error("custom rules: " + LastReport);
				Notify(LastReport);
				return;
			}
			LoadCheck.Write(marker, new LoadCheck { Hash = hash, World = worldId, StartedUtc = DateTime.UtcNow });
			_markerPath = marker;
			_current = rules;
			var outcome = CustomRulesEngine.ApplyDefinitions(rules);
			_hostApplied = outcome.Applied;
			HostRules.Offer = outcome.Applied.Count == 0 ? null : new RulesOffer(hash, RulesWire.Encode((byte)SyncMsg.RulesOffer, hash, outcome.Applied), outcome.Applied.Count);
			report = Summary(rules, outcome);
			LastReport = report;
		}
		GameLog.Instance.Info("custom rules: " + report);
		foreach (var note in _lastProblems) GameLog.Instance.Warn("custom rules: " + note);
		if (HostRules.Offer != null) GameLog.Instance.Info("custom rules: live on this server: " + CustomRulesEngine.Probe(_hostApplied));
	}

	private static List<string> _lastProblems = new();

	private static string Summary(RulesDocument rules, CustomRulesEngine.Outcome outcome)
	{
		_lastProblems = outcome.Errors.Select(e => "refused " + e).Concat(outcome.Notes.Select(n => "clamped " + n)).ToList();
		string text = $"{rules.Count} override(s) ({rules.Hash()}): {outcome.Applied.Count} definition value(s) changed on {outcome.Definitions} definition(s)"
			+ (rules.SessionSettings.Count > 0 ? $", session settings for {rules.SessionSettings.Count} component(s) once the world is read" : "")
			+ (outcome.Notes.Count > 0 ? $", {outcome.Notes.Count} note(s): clamped to a safe value or partly applied" : "")
			+ (outcome.Errors.Count > 0 ? $", {outcome.Errors.Count} refused (see the log)" : "") + ".";
		if (outcome.Errors.Count > 0) Notify("Custom rules: " + outcome.Errors.Count + " override(s) refused, first: " + outcome.Errors[0]);
		return text;
	}

	public static IReadOnlyList<string> Problems => _lastProblems;

	private static void Notify(string message)
	{
		try { if (!Dedicated.DedicatedRuntime.Requested) SyncEngine.MpMenu.Report(message); } catch (Exception) { }
	}

	// The session builders are read from the save by now: write the session settings, once per builder.
	private static void InjectPrefix(SessionBuilder sessionBuilder)
	{
		string refusal = null;
		InjectRules(sessionBuilder, ref refusal);
		if (refusal == null) return;
		GameLog.Instance.Error("custom rules: " + refusal);
		lock (Gate)
		{
			CustomRulesEngine.Revert();
			_received = null;
			_receivedFor = null;
			_deferred = 0;
			_joinCheck = false;
			_refusal = refusal;
			_refusalFor = NetHost.NetSession.Current?.Client;
		}
		// The world does not load: the player goes back to the menu with the reason (as the server's refusal would).
		throw new InvalidOperationException(refusal);
	}

	private static void InjectRules(SessionBuilder sessionBuilder, ref string refusal)
	{
#if !PULSAR
		// Test rig: a world that fails to load while it has custom rules (the fallback's proof).
		if (sessionBuilder.IsServer && Current != null && MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "selftest-rules-fail"))
			throw new InvalidOperationException("selftest: simulated load failure in GameModeObjectBuilder.SuperDampeners");
#endif
		try
		{
			if (!sessionBuilder.IsServer)
			{
				List<AppliedRule> received;
				int deferred;
				bool joinCheck;
				lock (Gate) (received, deferred, joinCheck) = (_received, _deferred, _joinCheck);
				// Again here, in case the game swapped definitions while the player's world was loading; and here first for
				// values whose definitions were not loaded when the rules came: the world's definitions (with the server's
				// mods) are in now, so what does not fit now never will, and this world would run out of step.
				if (received != null)
				{
					var problems = CustomRulesEngine.ApplyExact(received, revertFirst: false);
					foreach (var problem in problems) GameLog.Instance.Warn("custom rules: " + problem);
					if (joinCheck && problems.Count > 0)
						refusal = $"Your game could not apply this server's custom rules, so joining would put you out of step with the server: {problems.Count} of the server's {received.Count} custom values do not fit this game: {problems[0]}";
					else
					{
						lock (Gate) (_deferred, _joinCheck) = (0, false);
						if (deferred > 0) GameLog.Instance.Info($"custom rules: the {deferred} value(s) that waited for the world's definitions are applied");
						GameLog.Instance.Info("custom rules: live in this player's world: " + CustomRulesEngine.Probe(received));
					}
				}
#if !PULSAR
				// Test rig: the session settings this player's world got from the server's snapshot.
				if (SelfTest.Requested) GameLog.Instance.Info("selftest: player's session builders: " + string.Join(", ", (sessionBuilder.SessionComponents.Components ?? new()).Select(c => c.ObjectBuilder?.GetType().Name ?? c.Type?.Name)));
				if (SelfTest.Requested)
					foreach (var component in sessionBuilder.SessionComponents.Components ?? new List<Keen.VRage.DCS.Builders.EntityBuilder.ComponentBuildInfo>())
						if (component.ObjectBuilder is IAccessible ob && ob.GetType().Name is "GameModeObjectBuilder" or "PCUSessionComponentObjectBuilder" or "SunSessionComponentObjectBuilder" or "ProgressionObjectBuilder")
							GameLog.Instance.Info($"selftest: player's session {ob.GetType().Name}: " + string.Join(", ", CustomRulesEngine.Describe(ob).Select(p => p.Key + "=" + JsonSerializer.Serialize(p.Value).Split("\"Value\":").Last().TrimEnd('}'))));
#endif
				return;
			}
			var rules = Current;
			var outcome = new CustomRulesEngine.Outcome();
			foreach (var component in sessionBuilder.SessionComponents.Components ?? new List<Keen.VRage.DCS.Builders.EntityBuilder.ComponentBuildInfo>())
			{
				if (component.ObjectBuilder is not IAccessible builder) continue;
				string type = builder.GetType().FullName;
				if (rules != null && rules.SessionSettings.TryGetValue(type, out var fields) && !SessionDone.TryGetValue(builder, out _))
				{
					SessionDone.Add(builder, null);
					CustomRulesEngine.ApplySession(builder, fields, outcome);
				}
				lock (Gate) SessionSchema[type] = CustomRulesEngine.Describe(builder);
#if !PULSAR
				if (SelfTest.Requested && builder.GetType().Name is "GameModeObjectBuilder" or "PCUSessionComponentObjectBuilder" or "SunSessionComponentObjectBuilder" or "ProgressionObjectBuilder")
					GameLog.Instance.Info($"selftest: server's session {builder.GetType().Name}: " + string.Join(", ", CustomRulesEngine.Describe(builder).Select(p => p.Key + "=" + JsonSerializer.Serialize(p.Value).Split("\"Value\":").Last().TrimEnd('}'))));
#endif
			}
			if (rules != null)
			{
				var present = new HashSet<string>(SessionSchema.Keys);
				foreach (var missing in rules.SessionSettings.Keys.Where(k => !present.Contains(k)))
					outcome.Errors.Add($"[{missing}] this world has no such session component; skipped.");
			}
			foreach (var e in outcome.Errors) GameLog.Instance.Warn("custom rules: refused " + e);
			foreach (var n in outcome.Notes) GameLog.Instance.Warn("custom rules: clamped " + n);
			if (outcome.Errors.Count + outcome.Notes.Count > 0)
			{
				_lastProblems = _lastProblems.Concat(outcome.Errors.Select(e => "refused " + e)).Concat(outcome.Notes.Select(n => "clamped " + n)).ToList();
				if (outcome.Errors.Count > 0) Notify("Custom rules: session setting refused: " + outcome.Errors[0]);
			}
		}
		catch (Exception ex) { GameLog.Instance.Error("custom rules: session settings failed: " + ex); }
	}

	// Called every frame once a world runs (null), or once with the reason a load failed.
	private static void LoadFinishedPrefix(Exception errorInfo)
	{
		string marker;
		lock (Gate) marker = _markerPath;
		if (marker != null) Finished(errorInfo);
		if (_unsaved && errorInfo == null) CarryToFirstSave();
#if !PULSAR
		if (errorInfo == null) LiveChangeTest();
#endif
	}

#if !PULSAR
	private static long _liveTestAtMs;

	// Test rig: -y51online:selftest-rules-live changes the running world's rules 90 s after it loads (jetpack x2 instead of
	// its value) and sends them to the players in it, the way the Rules page's Apply now does.
	private static void LiveChangeTest()
	{
		if (_liveTestAtMs < 0 || !MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "selftest-rules-live")) return;
		long now = Environment.TickCount64;
		if (_liveTestAtMs == 0) { _liveTestAtMs = now + 90000; return; }
		if (now < _liveTestAtMs) return;
		_liveTestAtMs = -1;
		var rules = (Current ?? new RulesDocument()).Clone();
		rules.Set(false, "Keen.Game2.Simulation.WorldObjects.Characters.JetpackMovementComponentDefinition", "MaxThrust", JsonDocument.Parse("{\"$mul\":2}").RootElement);
		GameLog.Instance.Info("selftest: changing the running world's rules (jetpack x2) and sending them to the players in it");
		ApplyLive(rules, offer => NetHost.NetSession.Current?.Host?.BroadcastRules(offer));
	}
#endif

	// A new world's rules belong to it once it has a save: the next load of that save gets them, like any saved world.
	private static void CarryToFirstSave()
	{
		long now = Environment.TickCount64;
		if (now < _nextSaveCheckMs) return;
		_nextSaveCheckMs = now + 2000;
		string id = MpPlugin.App?.Server?.Entity.Get<WorldSessionComponent>()?.SaveContainerId?.ToString();
		if (string.IsNullOrEmpty(id) || WorldRulesStore.IsNew(id)) return;
		RulesDocument rules;
		lock (Gate) { rules = _current; _unsaved = false; _worldLabel = id; }
		if (WorldRulesStore.CarryTo(id, rules)) GameLog.Instance.Info($"custom rules: the new world was saved as '{id}'; its rules are kept with that save");
	}

	/// <summary>
	/// Co-op "Apply now": the definitions change in the running world at once (players get them too); session settings are
	/// read when a world loads, so those apply at the next load. Returns the report.
	/// </summary>
	public static string ApplyLive(RulesDocument rules, Action<RulesOffer> sendToPlayers)
	{
		string report;
		lock (Gate)
		{
			if (MpPlugin.App?.Server == null) return "No world of yours is running.";
			_current = rules == null || rules.IsEmpty ? null : rules;
			var outcome = CustomRulesEngine.ApplyDefinitions(_current);
			_hostApplied = outcome.Applied;
			string hash = _current?.Hash() ?? "";
			HostRules.Offer = outcome.Applied.Count == 0 ? null : new RulesOffer(hash, RulesWire.Encode((byte)SyncMsg.RulesOffer, hash, outcome.Applied), outcome.Applied.Count);
			report = _current == null ? "Custom rules removed from the running world." : Summary(_current, outcome);
			LastReport = report;
		}
		var offer = HostRules.Offer ?? new RulesOffer("", RulesWire.Encode((byte)SyncMsg.RulesOffer, "", new List<AppliedRule>()), 0);
		sendToPlayers?.Invoke(offer);
		GameLog.Instance.Info("custom rules: applied now: " + report + (_hostApplied.Count > 0 ? " Live: " + CustomRulesEngine.Probe(_hostApplied) : ""));
		return report;
	}

	/// <summary>The world finished loading (error null) or failed: the load check is cleared or keeps the reason.</summary>
	public static void Finished(Exception error)
	{
		string marker;
		lock (Gate) { marker = _markerPath; _markerPath = null; }
		if (marker == null) return;
		if (error == null)
		{
			LoadCheck.Delete(marker);
			if (_hostApplied.Count > 0) GameLog.Instance.Info("custom rules: the world loaded with its rules; live: " + CustomRulesEngine.Probe(_hostApplied));
			return;
		}
		var check = LoadCheck.Read(marker) ?? new LoadCheck();
		check.Error = (error.GetBaseException().GetType().Name + ": " + error.GetBaseException().Message + " " + string.Join(" ", (error.GetBaseException().StackTrace ?? "").Split('\n').Take(4).Select(l => l.Trim()))).Trim();
		LoadCheck.Write(marker, check);
		string text = "The world failed to load with custom rules. The next load of this world starts without them. " + check.Error;
		GameLog.Instance.Error("custom rules: " + text);
		Notify(text);
	}

	private static string Suspects(RulesDocument rules, string error)
	{
		var all = rules.SessionSettings.SelectMany(s => s.Value.Keys.Select(f => (Key: s.Key, Field: f)))
			.Concat(rules.DefinitionSettings.SelectMany(s => s.Value.Keys.Select(f => (Key: s.Key, Field: f)))).ToList();
		string Short(string key) => key.Split('#')[0].Split('.').Last() + (key.Contains('#') ? "#" + key.Split('#')[1] : "");
		var named = error == null ? new List<(string Key, string Field)>()
			: all.Where(o => error.Contains(Short(o.Key).Split('#')[0], StringComparison.Ordinal) || error.Contains(o.Field.Split('.')[0], StringComparison.Ordinal)).ToList();
		var list = (named.Count > 0 ? named : all).Take(8).Select(o => Short(o.Key) + "." + o.Field).ToList();
		int more = (named.Count > 0 ? named.Count : all.Count) - list.Count;
		return (named.Count > 0 ? "named in the error: " : "") + string.Join(", ", list) + (more > 0 ? $" and {more} more" : "");
	}

	// A player joining a server: this game's own world rules come off, the server's arrive with the join.
	private static void JoiningPrefix()
	{
		lock (Gate)
		{
			_current = null;
			_markerPath = null;
			HostRules.Offer = null;
			_refusal = null;
			// SE2 Online's own join: the server's rules came on this connection before the game started loading the world.
			if (_received != null && _receivedFor != null && ReferenceEquals(_receivedFor, NetHost.NetSession.Current?.Client))
			{
				GameLog.Instance.Info($"custom rules: keeping the server's {_received.Count} definition value(s) for this join");
				return;
			}
			_received = null;
			_receivedFor = null;
			_deferred = 0;
			_joinCheck = false;
			_refusal = null;
			int restored = CustomRulesEngine.Revert();
			if (restored > 0) GameLog.Instance.Info($"custom rules: put {restored} definition value(s) back to the game's own before joining");
		}
	}

	// Main thread (ClientSession.ApplyRulesOffers): before the host sends the world (it waits for this answer), or live.
	private static string ApplyReceived(string hash, List<AppliedRule> rules)
	{
		lock (Gate)
		{
			// Before the world loads (SE2 Online's join gets the rules first), the world's and the mods' definitions are not
			// in the game yet: values for them wait and are checked when the world loads (InjectPrefix). In a world, every
			// definition the server's values name must be here.
			var absent = JoinOffer() ? new List<AppliedRule>() : null;
			var problems = CustomRulesEngine.ApplyExact(rules, revertFirst: true, absent);
			_received = rules;
			_receivedFor = NetHost.NetSession.Current?.Client;
			_deferred = absent?.Count ?? 0;
			_joinCheck = absent != null;
			_refusal = null;
			if (problems.Count == 0)
			{
				GameLog.Instance.Info($"custom rules: applied the server's {rules.Count - _deferred} definition value(s) ({hash}): " + CustomRulesEngine.Probe(rules)
					+ (_deferred > 0 ? $"; {_deferred} more wait for the world's definitions (they load with the world)" : ""));
				return "";
			}
			CustomRulesEngine.Revert();
			_received = null;
			_receivedFor = null;
			_deferred = 0;
			_joinCheck = false;
			foreach (var p in problems.Take(20)) GameLog.Instance.Warn("custom rules: " + p);
			return $"{problems.Count} of the server's {rules.Count} custom values do not fit this game: {problems[0]}" + (problems.Count > 1 ? $" (and {problems.Count - 1} more, see the log)" : "");
		}
	}

	// Rules that come with a join (the server has not accepted this player yet, so no world of its is loading or loaded);
	// rules changed while the player is in come after the accept.
	private static bool JoinOffer() => NetHost.NetSession.Current?.Client is { } client && client.CurrentPhase < ClientSession.Phase.Accepted;

	/// <summary>Dedicated: the world is up; clear the load check and write the dumps the runner's editor reads.</summary>
	public static void WriteDumps(string settingsPath, string definitionsPath, bool writeDefinitions)
	{
		Finished(null);
		Directory.CreateDirectory(Path.GetDirectoryName(settingsPath));
		lock (Gate) File.WriteAllText(settingsPath, JsonSerializer.Serialize(SessionSchema, Dedicated.DedicatedConfig.Json));
		if (!writeDefinitions) return;
		var catalog = CustomRulesEngine.LiveCatalog(Array.Empty<KeyValuePair<string, Dictionary<string, object>>>());
		var schema = new SortedDictionary<string, object>(StringComparer.Ordinal);
		foreach (var list in catalog.Definitions.Values)
			foreach (var e in list)
				schema[e.Key] = e.LoadTime == null
					? (object)new { e.DebugName, Settings = e.Fields.ToDictionary(f => f.Key, f => (object)new { f.Value.Type, f.Value.Writable, Value = f.Value.Value }) }
					: new { e.DebugName, e.LoadTime, Settings = e.Fields.ToDictionary(f => f.Key, f => (object)new { f.Value.Type, f.Value.Writable, Value = f.Value.Value }) };
		Directory.CreateDirectory(Path.GetDirectoryName(definitionsPath));
		File.WriteAllText(definitionsPath, JsonSerializer.Serialize(schema, Dedicated.DedicatedConfig.Json));
	}

	/// <summary>Session components the loaded server world has, with the game's values (for the in-game editor).</summary>
	public static List<KeyValuePair<string, Dictionary<string, object>>> SessionComponents()
	{
		lock (Gate) return SessionSchema.ToList();
	}
}

/// <summary>A world started loading with these rules and has not finished yet (or failed: Error says how).</summary>
internal sealed class LoadCheck
{
	public string Hash { get; set; }
	public string World { get; set; }
	public DateTime StartedUtc { get; set; }
	public string Error { get; set; }
	public bool Skipped { get; set; }

	public static LoadCheck Read(string path)
	{
		try { return path != null && File.Exists(path) ? JsonSerializer.Deserialize<LoadCheck>(File.ReadAllText(path)) : null; }
		catch (Exception) { return null; }
	}

	public static void Write(string path, LoadCheck check)
	{
		if (path == null) return;
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(path));
			File.WriteAllText(path, JsonSerializer.Serialize(check, new JsonSerializerOptions { WriteIndented = true }));
		}
		catch (Exception ex) { GameLog.Instance.Warn($"custom rules: could not write the load check {path} ({ex.Message})"); }
	}

	public static void Delete(string path)
	{
		try { if (path != null && File.Exists(path)) File.Delete(path); }
		catch (Exception ex) { GameLog.Instance.Warn($"custom rules: could not clear the load check {path} ({ex.Message})"); }
	}
}

/// <summary>A co-op or single-player world's rules: one file per save in the plugin's data folder.</summary>
internal static class WorldRulesStore
{
	public static string Folder => Path.Combine(PluginPaths.DataDir, "WorldRules");

	public static string FileFor(string worldId) => Path.Combine(Folder, Safe(worldId) + ".se2rules.json");

	public static string CheckFor(string worldId) => Path.Combine(Folder, Safe(worldId) + ".loadcheck.json");

	private static string Safe(string id)
	{
		if (string.IsNullOrWhiteSpace(id)) return "_new-world";
		var chars = id.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == '.' ? '_' : c).ToArray();
		return new string(chars);
	}

	public const string NewWorld = "";

	/// <summary>A world that is not a save yet: an empty one, or one started from the game's content ($Content:...).</summary>
	public static bool IsNew(string worldId) => string.IsNullOrWhiteSpace(worldId) || worldId.StartsWith("$Content:", StringComparison.Ordinal);

	/// <summary>A saved world's own rules; a new world gets the rules set for new worlds (they move with it at its first save).</summary>
	public static (RulesDocument, string) ForWorld(string worldId)
	{
		if (IsNew(worldId)) worldId = NewWorld;
		string path = FileFor(worldId);
		try { return (File.Exists(path) ? RulesDocument.Load(path) : null, CheckFor(worldId)); }
		catch (Exception ex)
		{
			GameLog.Instance.Error($"custom rules: {path} cannot be read ({ex.Message}); this world loads without custom rules");
			return (null, null);
		}
	}

	/// <summary>Copies the new-world rules to a world's first save (only if it has none of its own yet).</summary>
	public static bool CarryTo(string worldId, RulesDocument rules)
	{
		string path = FileFor(worldId);
		if (rules == null || rules.IsEmpty || File.Exists(path)) return false;
		try { rules.Save(path); return true; }
		catch (Exception ex) { GameLog.Instance.Warn($"custom rules: could not keep the rules for '{worldId}' ({ex.Message})"); return false; }
	}

	/// <summary>Clears a failed-load check, so the next load tries the rules again.</summary>
	public static void Retry(string worldId) => LoadCheck.Delete(CheckFor(worldId));

	public static LoadCheck Check(string worldId) => LoadCheck.Read(CheckFor(worldId));
}
