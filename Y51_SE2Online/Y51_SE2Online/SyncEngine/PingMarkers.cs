using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Keen.Game2.Client.GameSystems.CameraSystems;
using Keen.Game2.Client.GameSystems.GPS;
using Keen.Game2.Client.GameSystems.PlayerControl;
using Keen.Game2.Client.WorldObjects.Tools;
using Keen.Game2.Simulation.GameSystems.GPS;
using Keen.Game2.Simulation.WorldObjects.Characters;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.Core.Render;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Collections;
using Keen.VRage.Library.Definitions;
using Keen.VRage.Library.Filesystem;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Library.Utils;
using Keen.VRage.Voxels.Components;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

/// <summary>The pings this player sees, as native HUD markers (bunching and off-screen arrows stay the game's).</summary>
internal sealed class PingMarkerProvider : Component, IGPSMarkersProvider
{
	public volatile ObservableList<GPSMarker> Current = new ObservableList<GPSMarker>();

	ObservableList<GPSMarker> IGPSMarkersProvider.Markers => Current;
}

/// <summary>The shipped marker icons (DDS named .png, PlayerIcon's format), mirrored into the profile and registered once.</summary>
internal static class PingIcons
{
	public const string Folder = "Textures/GUI/Icons/SE2Online/Markers/";
	private static readonly Dictionary<string, ResourceHandle<TextureAsset>?> Cache = new();
	private static MethodInfo _createHandle;
	private static bool _warned;

	public static ResourceHandle<TextureAsset> Get(string name, ResourceHandle<TextureAsset> fallback)
	{
		if (!Cache.TryGetValue(name, out var icon))
		{
			try { icon = Load(name); }
			catch (Exception ex)
			{
				icon = null;
				if (!_warned) { _warned = true; GameLog.Instance.Warn($"pings: marker icon {name} unavailable, using the GPS icon ({ex.GetType().Name}: {ex.Message})"); }
			}
			Cache[name] = icon;
		}
		return icon ?? fallback;
	}

	/// <summary>A real PNG (white glyph) for the Avalonia compass strip and feed.</summary>
	public static string HudPath(string glyph) => PluginPaths.ContentDir is { } content ? Path.Combine(content, Folder + "Hud/" + glyph + ".png") : null;

	private static ResourceHandle<TextureAsset>? Load(string name)
	{
		string relative = Folder + name + ".png", appDataPath = "Y51_SE2Online/Content/" + relative;
		string source = PluginPaths.ContentDir is { } content ? Path.Combine(content, relative) : null;
		if (source == null || !File.Exists(source))
		{
			if (!_warned) { _warned = true; GameLog.Instance.Warn("pings: " + source + " is missing; pings use the GPS icon"); }
			return null;
		}
		// The game's resource system only registers files under its own roots, so the icon is mirrored into the profile.
		var appData = Singleton<FileSystem>.Instance.GetAppDataFiles();
		byte[] bytes = File.ReadAllBytes(source);
		if (!appData.FileExists(appDataPath) || !Same(appData, appDataPath, bytes))
		{
			appData.CreateDirectory(Path.GetDirectoryName(appDataPath).Replace('\\', '/'));
			using var stream = appData.Open(appDataPath, FileMode.Create, FileAccess.Write);
			stream.Write(bytes);
		}
		_createHandle ??= typeof(FileHandle).GetMethod("CreateNormalizedFileHandle", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
			?? throw new MissingMethodException(nameof(FileHandle), "CreateNormalizedFileHandle");
		var file = (FileHandle)_createHandle.Invoke(null, new object[] { RootPath.AppData, appDataPath });
		return (ResourceHandle<TextureAsset>)ResourceHandle.GetOrRegister(file);
	}

	private static bool Same(RootFileSystem files, string path, byte[] bytes)
	{
		using var stream = files.OpenRead(path);
		using var memory = new MemoryStream();
		stream.CopyTo(memory);
		return memory.GetBuffer().AsSpan(0, (int)memory.Length).SequenceEqual(bytes);
	}
}

/// <summary>What the compass strip and the feed show, rebuilt at 10 Hz.</summary>
/// <param name="CompassFade">0: no strip (in space, or at a pole); up to 1 on a planet.</param>
/// <param name="Facing">The camera's compass heading on the planet (0 north, clockwise).</param>
internal sealed record PingHudState(PingHudState.Tick[] Ticks, PingHudState.Row[] Feed, PingHudState.Pip[] Pips, double CompassFade, double Facing)
{
	public sealed record Tick(string Glyph, ColorSRGB Colour, double X, string Distance);
	public sealed record Row(string Glyph, ColorSRGB Colour, string Text, double Alpha, uint Id);
	public sealed record Pip(string Variant, double X, double Y, double Angle);
	public static readonly PingHudState Empty = new(Array.Empty<Tick>(), Array.Empty<Row>(), Array.Empty<Pip>(), 0, 0);
}

/// <summary>
/// Design 10.3: pings and player nametags get a relevance score at 10 Hz (<see cref="MarkerRelevance.Decide"/>) and are drawn
/// Full, as a Dot or hidden; the off-screen or collapsed ones go to the compass strip. Vanilla markers never come here.
/// </summary>
internal static class PingMarkers
{
	internal const string MarkerIdentifier = "Y51_SE2Online.Ping";
	private static readonly ColorSRGB AdminColour = new(240, 160, 30);
	private static readonly FieldInfo EntityBackingField = EngineMembers.Field(HookTargets.Reflected.Single(m => m.Id == "component-entity"));

	private sealed class Live
	{
		public PingBook.Entry Entry;
		public GPSMarker Marker;
		public ColorSRGB Colour;
	}

	private static readonly Dictionary<uint, Live> Markers = new();
	private static Entity _sessionEntity;
	private static PingMarkerProvider _provider;
	private static bool _failed;
	private static long _retryAtMs;

	public static volatile PingHudState Hud = PingHudState.Empty;

	/// <summary>The camera and the local player as last seen, for aiming at a ping (Ctrl+ping key).</summary>
	internal static (Vector3D Eye, Vector3 Forward)? Camera;

	public static void Reset()
	{
		Markers.Clear();
		if (_provider != null) _provider.Current = new ObservableList<GPSMarker>();
		_provider = null;
		_sessionEntity = null;
		NametagRelevance.Reset();
		Hud = PingHudState.Empty;
		Camera = null;
	}

	/// <summary>The live ping nearest the crosshair within <see cref="PingLogic.KeepAimDegrees"/>, for Ctrl+ping key.</summary>
	public static PingShow Aimed()
	{
		if (Camera is not { } cam) return null;
		PingShow best = null;
		double bestAngle = PingLogic.KeepAimDegrees;
		foreach (var live in Markers.Values)
		{
			var s = live.Entry.Show;
			double angle = PingLogic.AimDegrees(T(cam.Forward), (s.X - cam.Eye.X, s.Y - cam.Eye.Y, s.Z - cam.Eye.Z));
			if (angle <= bestAngle) { bestAngle = angle; best = s; }
		}
		return best;
	}

	/// <summary>Main thread, 10 Hz.</summary>
	public static void Update(Session session, PingBook book, MarkerChoice choice, long now)
	{
		if (_failed && now < _retryAtMs) return;
		_failed = false;
		try { UpdateCore(session, book, choice, now); }
		catch (Exception ex)
		{
			_failed = true;
			_retryAtMs = now + 2000;
			NametagRelevance.Decisions = null;
			GameLog.Instance.WarnOnce("ping-markers", "pings: marker update failed, retrying (nametags show as before): " + ex);
		}
	}

	private static void UpdateCore(Session session, PingBook book, MarkerChoice choice, long now)
	{
		var players = session.SessionComponents.TryGet<ClientPlayersSessionComponent>();
		var controller = players?.LocalPlayerController;
		var cameraEntity = controller?.CameraSystem?.RenderCameraEntity;
		if (players == null || controller == null || cameraEntity == null) { Camera = null; return; }
		if (!ReferenceEquals(players.Entity, _sessionEntity)) Attach(players.Entity);
		var config = Singleton<DefinitionManager>.Instance.GetConfiguration<GPSMarkersConfiguration>();
		var view = cameraEntity.Data.GetWorldTransform();
		Vector3 forward = view.Orientation.GetForward(), up = view.Orientation.GetUp();
		Vector3 right = Vector3.Cross(forward, up);
		Camera = (view.Position, forward);
		var camera = cameraEntity.TryGet<CameraComponent>();
		double fov = camera?.FieldOfView ?? 1.2, aspect = camera?.AspectRatio ?? 16 / 9.0;
		Vector3D anchor = GPSMarkerHelpers.GetAnchorPosition(controller);
		MarkerFilter.SetAnchor(anchor);
		var defaults = MarkerFilter.Defaults;
		var settings = MpMenu.Settings;
		var thresholds = new MarkerThresholds(AimDegrees: settings.MarkerAimDegrees, FullCount: settings.MarkerFullCount);
		var activity = Activity(controller);

		bool changed = SyncMarkers(book, config, now);

		var nametagSource = (_sessionEntity?.Components.OfType<NametagMarkerProvider>().FirstOrDefault())?.Current;
		var nametags = nametagSource?.Where(m => m != null).ToList() ?? new List<GPSMarker>();
		var pings = Markers.Values.ToList();
		double pingRange = MarkerChoice.MaxRange(defaults, MarkerCategory.Pings), tagRange = MarkerChoice.MaxRange(defaults, MarkerCategory.Players);
		var inputs = new List<MarkerInput>(pings.Count + nametags.Count);
		var onScreen = new List<bool>(inputs.Capacity);
		var positions = new List<Vector3D>(inputs.Capacity);
		foreach (var p in pings)
		{
			var s = p.Entry.Show;
			var at = new Vector3D(s.X, s.Y, s.Z);
			Add(new MarkerInput(false, s.Kind, s.Relation, p.Entry.AgeS(now), s.LifetimeS, Vector3D.Distance(anchor, at), pingRange > 0 ? pingRange : PingLogic.DefaultRangeMeters, 0), at);
		}
		foreach (var tag in nametags)
		{
			var (relation, mine) = NametagRelevance.RelationOf(tag.Color);
			Add(new MarkerInput(true, PingKind.Player, relation, 0, 0, Vector3D.Distance(anchor, tag.Position), tagRange > 0 ? tagRange : NametagRange.Metres, 0, mine), tag.Position);
		}
		void Add(MarkerInput input, Vector3D at)
		{
			var to = T(at - view.Position);
			inputs.Add(input with { AimDegrees = PingLogic.AimDegrees(T(forward), to) });
			onScreen.Add(PingLogic.OnScreen(T(forward), T(right), T(up), to, fov, aspect));
			positions.Add(at);
		}

		var results = MarkerRelevance.Decide(inputs, choice.Mode, activity, thresholds, choice.On(MarkerCategory.Pings), choice.On(MarkerCategory.Players));
		var ticks = MarkerRelevance.CompassTicks(inputs, results, onScreen, PingLogic.CompassTicks);

		for (int i = 0; i < pings.Count; i++)
		{
			var live = pings[i];
			var r = results[i];
			bool outOfRange = pingRange > 0 && inputs[i].DistanceMeters > pingRange;
			bool hidden = r.Display == MarkerDisplay.Hidden || outOfRange || r.Display == MarkerDisplay.Dot && !onScreen[i];
			bool dot = r.Display == MarkerDisplay.Dot;
			var show = live.Entry.Show;
			var icon = PingIcons.Get(PingLogic.IconName(show.Kind, show.Relation, dot), config.PlayerCreatedMarkerIcon);
			string name = r.Display == MarkerDisplay.Full ? PingLogic.WorldName(show, inputs[i].AimDegrees <= thresholds.AimDegrees) : "";
			var colour = live.Colour;
			colour = new ColorSRGB(colour.R, colour.G, colour.B, PingLogic.AlphaByte(r.Alpha));
			var m = live.Marker;
			if (m.Hidden != hidden) m.Hidden = hidden;
			if (!m.Icon.Equals(icon)) m.Icon = icon;
			if (m.Name != name) m.Name = name;
			if (!m.Color.Equals(colour)) m.Color = colour;
		}

		var decisions = new Dictionary<GPSMarker, NametagRelevance.Decision>(ReferenceEqualityComparer.Instance);
		for (int j = 0; j < nametags.Count; j++)
		{
			int i = pings.Count + j;
			var tag = nametags[j];
			var r = results[i];
			bool outOfRange = tagRange > 0 && inputs[i].DistanceMeters > tagRange;
			bool hidden = r.Display == MarkerDisplay.Hidden || outOfRange || r.Display == MarkerDisplay.Dot && !onScreen[i];
			var dotIcon = r.Display == MarkerDisplay.Dot ? PingIcons.Get("dot_" + PingLogic.Variant(PingKind.Player, inputs[i].Source), tag.Icon) : default(ResourceHandle<TextureAsset>?);
			decisions[tag] = new NametagRelevance.Decision(hidden, r.Display == MarkerDisplay.Full, dotIcon, PingLogic.AlphaByte(r.Alpha));
		}
		NametagRelevance.Decisions = decisions;

		if (changed)
		{
			var list = new ObservableList<GPSMarker>();
			foreach (var live in Markers.Values) list.Add(live.Marker);
			if (_provider != null) _provider.Current = list;
		}

		// The compass strip only on or near a planet or moon; in space (or at a pole) off-screen markers get edge pips.
		var frame = Frame(session, anchor);
		double facing = frame is { } f ? PingLogic.Facing(f, T(forward), T(up)) : 0;
		var compass = new List<PingHudState.Tick>(ticks.Length);
		var pips = new List<PingHudState.Pip>();
		if (frame is { Fade: > 0 } planet)
		{
			foreach (int i in ticks)
			{
				var input = inputs[i];
				// From the camera, not the anchor: in a cockpit the anchor is the grid origin, tens of metres away.
				if (!PingLogic.TryBearing(planet, T(positions[i] - view.Position), out double bearing)) continue;
				string glyph = input.Nametag ? "player" : PingRules.Glyph(input.Kind);
				var colour = i < pings.Count ? pings[i].Colour : nametags[i - pings.Count].Color;
				compass.Add(new PingHudState.Tick(glyph, new ColorSRGB(colour.R, colour.G, colour.B, (byte)255), PingLogic.CompassX(PingLogic.Relative(bearing, facing)), PingRules.DistanceText(input.DistanceMeters)));
			}
		}
		else
		{
			// Full markers off screen keep the native ellipse arrow (DrawIcon draws ArrowIcon for any outside-screen marker).
			foreach (int i in Enumerable.Range(0, inputs.Count).Where(i => results[i].Display == MarkerDisplay.Dot && !onScreen[i]).OrderBy(i => inputs[i].DistanceMeters).Take(PingLogic.CompassTicks))
			{
				var to = T(positions[i] - view.Position);
				var (x, y, angle) = PingLogic.EdgePip(PingLogic.Dot(to, T(right)), PingLogic.Dot(to, T(up)), PingLogic.Dot(to, T(forward)));
				pips.Add(new PingHudState.Pip(PingLogic.Variant(inputs[i].Nametag ? PingKind.Player : inputs[i].Kind, inputs[i].Source), x, y, angle));
			}
		}
		var feed = book.Feed(now).Select(r => new PingHudState.Row(PingRules.Glyph(r.Entry.Show.Kind), ColourOf(r.Entry.Show),
			PingLogic.FeedText(r.Entry.Show, Vector3D.Distance(anchor, new Vector3D(r.Entry.Show.X, r.Entry.Show.Y, r.Entry.Show.Z))), r.Alpha, r.Entry.Show.Id)).ToArray();
		Hud = new PingHudState(compass.ToArray(), feed, pips.ToArray(), frame is { } shown ? shown.Fade : 0, facing);
	}

	private static readonly bool CompassOn = MpPlugin.LaunchArgs.Contains(Y51_SE2Online.Core.MpOptions.Prefix + "compass:on");

	/// <summary>The horizon frame on the closest planet, by the flight HUD's own rule (FlightHUDViewModel.Update: in space when
	/// farther from the centre than MaxAltimeterRadius, via PlanetTrackerSessionComponent.TryGetClosestPlanet).</summary>
	private static PingLogic.PlanetFrame? Frame(Session session, Vector3D anchor)
	{
		// The compass strip is off for 0.18.0 (edge pips everywhere); -y51online:compass:on brings it back for testing.
		if (!CompassOn) return null;
		try
		{
			if (session.SessionComponents.TryGet<PlanetTrackerSessionComponent>()?.TryGetClosestPlanet(anchor) is not { } closest) return null;
			var transform = closest.Planet.Transform;
			// Planet latitude is the planet-local Y of the surface direction (SampledPlanetPosition.GetGeographic): north is local up.
			return PingLogic.Frame(T(anchor), T(transform.Position), T(Quaternion.GetUp(transform.Orientation)), closest.Planet.Component.MaxAltimeterRadius);
		}
		catch (Exception) { return null; }
	}

	// Adds, moves and drops markers to match the book; true when the provider's list must change.
	private static bool SyncMarkers(PingBook book, GPSMarkersConfiguration config, long now)
	{
		bool changed = false;
		var live = book.Live.ToDictionary(e => e.Show.Id);
		foreach (uint gone in Markers.Keys.Where(id => !live.ContainsKey(id)).ToList()) { Markers.Remove(gone); changed = true; }
		foreach (var (id, entry) in live)
		{
			if (Markers.TryGetValue(id, out var existing) && ReferenceEquals(existing.Entry, entry)) continue;
			var show = entry.Show;
			var colour = ColourOf(show);
			var icon = PingIcons.Get(PingLogic.IconName(show.Kind, show.Relation, false), config.PlayerCreatedMarkerIcon);
			var marker = new GPSMarker(config.PlayerMadeMarkerType, PingLogic.WorldName(show, false), "SE2 Online ping from " + show.FromName, new Vector3D(show.X, show.Y, show.Z),
				icon, colour, alwaysShow: true, hidden: false, removeWithEntity: false, MarkerIdentifier, showOnMap: false);
			Markers[id] = new Live { Entry = entry, Marker = marker, Colour = colour };
			changed = true;
		}
		return changed;
	}

	/// <summary>Danger is always red; otherwise the pinger's group colour when it has a readable one, else the relation colour.</summary>
	internal static ColorSRGB ColourOf(PingShow show)
	{
		if (show.Kind == PingKind.Danger) return MarkerColors.Enemy;
		if (AdminAccess.StyleOf(show.Group) is { } style && GroupStyle.Readable(style.NameColor) is var (r, g, b)) return new ColorSRGB(r, g, b);
		return show.Relation switch
		{
			PingRelation.Own => MarkerColors.Own,
			PingRelation.Faction => MarkerColors.Faction,
			PingRelation.Ally => MarkerColors.Ally,
			PingRelation.Admin => AdminColour,
			_ => MarkerColors.Neutral
		};
	}

	private static void Attach(Entity sessionEntity)
	{
		Markers.Clear();
		_sessionEntity = sessionEntity;
		var existing = sessionEntity.Components.OfType<PingMarkerProvider>().FirstOrDefault();
		if (existing != null) _provider = existing;
		else
		{
			var created = new PingMarkerProvider();
			// Attached by hand like NametagMarkerProvider, outside the DCS factory, so its Entity back-reference is set directly.
			EntityBackingField.SetValue(created, sessionEntity);
			sessionEntity.Components = sessionEntity.Components.Add(created);
			_provider = created;
		}
		_provider.Current = new ObservableList<GPSMarker>();
		GameLog.Instance.Info("pings: HUD marker provider attached to the client session");
	}

	/// <summary>What the local player is doing, read from their own character: tool in hand, seat or jetpack.</summary>
	private static PlayerActivity Activity(PlayerControllerComponent controller)
	{
		try
		{
			Entity character = null;
			bool seated = false;
			foreach (var controlled in controller.ControlledEntities)
			{
				var e = (controlled.Controllable as Component)?.Entity;
				if (e == null) continue;
				if (e.TryGet<CharacterComponent>() != null) character = e;
				else seated = true;
			}
			if (character == null) return seated ? PlayerActivity.Seated : PlayerActivity.None;
			var state = character.TryGet<CharacterMovementComponent>()?.CurrentMovementState;
			if (seated || state == CharacterMovementState.Sitting) return PlayerActivity.Seated;
			var tool = character.TryGet<ToolsComponent>()?.CurrentTool;
			if (tool != null)
			{
				if (tool.TryGet<DrillToolControllableComponent>() != null) return PlayerActivity.Drill;
				if (tool.TryGet<IntegrityToolControllableComponent>() != null) return PlayerActivity.WeldGrind;
				if (tool.TryGet<GunToolControllableComponent>() != null) return PlayerActivity.Weapon;
			}
			return state == CharacterMovementState.Jetpack ? PlayerActivity.Jetpack : PlayerActivity.None;
		}
		catch (Exception) { return PlayerActivity.None; }
	}

	private static (double X, double Y, double Z) T(Vector3D v) => (v.X, v.Y, v.Z);
	private static (double X, double Y, double Z) T(Vector3 v) => (v.X, v.Y, v.Z);
}

/// <summary>
/// The relevance rule for player nametags without touching <see cref="NametagOverlay"/>: the provider hands the renderer a
/// shadow copy of each tag with the decision applied (hidden, dot icon, no name, faded colour). Fields are written, not
/// properties, so nothing is notified per frame.
/// </summary>
internal static class NametagRelevance
{
	internal sealed record Decision(bool Hidden, bool Full, ResourceHandle<TextureAsset>? Dot, byte Alpha);

	/// <summary>Null: the tags show exactly as NametagOverlay made them.</summary>
	internal static volatile Dictionary<GPSMarker, Decision> Decisions;

	private static readonly ConditionalWeakTable<GPSMarker, GPSMarker> Shadows = new();
	// Render thread only. NametagOverlay publishes a new list on every change and never edits one it published, and it keeps
	// one GPSMarker per player (ClientId) for as long as that player has a tag, so the marker reference is the player's key:
	// decisions and shadows are looked up by it, never by list position. Decisions is built on the main thread and only
	// read here once published.
	private static ObservableList<GPSMarker> _source, _view;
	private static (GPSMarker Tag, GPSMarker Shadow)[] _pairs = Array.Empty<(GPSMarker, GPSMarker)>();
	private static volatile bool _failed;

	/// <summary>A new session gets a fresh try.</summary>
	internal static void Reset() { Decisions = null; _failed = false; }

	/// <summary>Render thread: what the nametag provider hands the HUD renderer.</summary>
	internal static ObservableList<GPSMarker> View(ObservableList<GPSMarker> source)
	{
		var decisions = Decisions;
		if (decisions == null || source == null || _failed) return source;
		try
		{
			if (!ReferenceEquals(source, _source) || _pairs.Length != source.Count)
			{
				var pairs = new List<(GPSMarker, GPSMarker)>(source.Count);
				var view = new ObservableList<GPSMarker>();
				foreach (var tag in source)
				{
					if (tag == null) continue;
					var shadow = Shadows.GetValue(tag, t => new GPSMarker(t.GPSMarkerType, t.Name, t.Description, t.Position, t.Icon, t.Color, t.AlwaysShow, t.Hidden, false, t.Identifier, false));
					pairs.Add((tag, shadow));
					view.Add(shadow);
				}
				_pairs = pairs.ToArray();
				_source = source;
				_view = view;
			}
			foreach (var (tag, shadow) in _pairs)
			{
				decisions.TryGetValue(tag, out var d);
				shadow._position = tag._position;
				shadow._hidden = tag._hidden || d?.Hidden == true;
				shadow._icon = d?.Dot ?? tag._icon;
				shadow._name = d == null || d.Full ? tag._name : "";
				var c = tag._color;
				shadow._color = d == null ? c : new ColorSRGB(c.R, c.G, c.B, Math.Min(c.A, d.Alpha));
				shadow.GPSMarkerType = tag.GPSMarkerType;
			}
			return _view;
		}
		catch (Exception ex)
		{
			_failed = true;
			GameLog.Instance.Warn("nametags: relevance view failed, tags show as before: " + ex.Message);
			return source;
		}
	}

	// Nametag colours come from MarkerColors by relation; matched back here rather than looked up again.
	// ponytail: an offline player's darkened colour reads as neutral; the relation table lookup if that matters.
	internal static (PingRelation Relation, bool FactionOrOwn) RelationOf(ColorSRGB c)
	{
		if (Same(c, MarkerColors.Own)) return (PingRelation.Own, true);
		if (Same(c, MarkerColors.Faction)) return (PingRelation.Faction, true);
		if (Same(c, MarkerColors.Ally)) return (PingRelation.Ally, false);
		return (PingRelation.Neutral, false);
	}

	private static bool Same(ColorSRGB a, ColorSRGB b) => a.R == b.R && a.G == b.G && a.B == b.B;
}
