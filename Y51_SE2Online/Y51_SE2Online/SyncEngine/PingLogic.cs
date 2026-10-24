using System;
using System.Collections.Generic;
using System.Linq;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

/// <summary>What the ping raycast hit, before it becomes a <see cref="PingKind"/>.</summary>
public enum PingHit : byte { Nothing, Voxel, Grid, Character }

/// <summary>A key that does one thing on a tap and another while held (the ping and marker filter keys).</summary>
public sealed class HoldGesture
{
	public enum Edge : byte { None, Tap, HoldStart, HoldEnd }

	private long _downAt = -1;
	private bool _held;

	public bool Held => _held;

	public Edge Update(bool down, long nowMs)
	{
		if (down)
		{
			if (_downAt < 0) { _downAt = nowMs; _held = false; return Edge.None; }
			if (!_held && nowMs - _downAt >= PingLogic.HoldMs) { _held = true; return Edge.HoldStart; }
			return Edge.None;
		}
		if (_downAt < 0) return Edge.None;
		bool held = _held;
		_downAt = -1;
		_held = false;
		return held ? Edge.HoldEnd : Edge.Tap;
	}

	public void Reset() { _downAt = -1; _held = false; }
}

/// <summary>The pings this player sees: one per sender (a new ping from the same player replaces it), and the recent feed.</summary>
public sealed class PingBook
{
	public sealed record Entry(PingShow Show, long ReceivedMs)
	{
		public double AgeS(long nowMs) => Math.Max(0, nowMs - ReceivedMs) / 1000.0;
		public bool Expired(long nowMs) => AgeS(nowMs) >= Math.Max(1, Show.LifetimeS);
	}

	private readonly Dictionary<uint, Entry> _live = new();
	private readonly List<Entry> _feed = new();

	public IReadOnlyCollection<Entry> Live => _live.Values;

	/// <summary>Takes a ping from the host; null when it removes one.</summary>
	public Entry Add(PingShow show, long nowMs)
	{
		if (show == null) return null;
		if (show.Remove)
		{
			_live.Remove(show.Id);
			_feed.RemoveAll(e => e.Show.Id == show.Id);
			return null;
		}
		var entry = new Entry(show, nowMs);
		_live[show.Id] = entry;
		_feed.Insert(0, entry);
		if (_feed.Count > MarkerRelevance.FeedRows) _feed.RemoveRange(MarkerRelevance.FeedRows, _feed.Count - MarkerRelevance.FeedRows);
		return entry;
	}

	/// <summary>Drops expired pings; true when any went.</summary>
	public bool Expire(long nowMs)
	{
		var gone = _live.Where(kv => kv.Value.Expired(nowMs)).Select(kv => kv.Key).ToList();
		foreach (uint id in gone) _live.Remove(id);
		_feed.RemoveAll(e => e.AgeS(nowMs) >= MarkerRelevance.FeedFadeTo);
		return gone.Count > 0;
	}

	public void Clear() { _live.Clear(); _feed.Clear(); }

	/// <summary>The newest ping still on screen (what /keep saves).</summary>
	public Entry Newest => _live.Values.OrderByDescending(e => e.ReceivedMs).FirstOrDefault();

	/// <summary>The feed rows, newest first, with their opacity: full for 6 s, then fading out by 10 s.</summary>
	public IEnumerable<(Entry Entry, double Alpha)> Feed(long nowMs)
		=> _feed.Select(e => (e, PingLogic.FeedAlpha(e.AgeS(nowMs)))).Where(r => r.Item2 > 0);
}

/// <summary>A player's marker filter as it applies: their own saved choice, or the server's defaults.</summary>
public sealed record MarkerChoice(MarkerMode Mode, IReadOnlyCollection<MarkerCategory> Off, bool ServerDefaults)
{
	public static readonly MarkerChoice Default = new(MarkerMode.Relevant, Array.Empty<MarkerCategory>(), true);

	public bool On(MarkerCategory category) => !Off.Contains(category);

	public static MarkerChoice Resolve(string mode, IReadOnlyCollection<string> off, MarkerDefaults defaults)
	{
		defaults ??= new MarkerDefaults();
		bool own = !string.IsNullOrWhiteSpace(mode) && PingRules.TryMode(mode, out _) || off != null;
		MarkerMode resolved = PingRules.TryMode(mode, out var m) ? m : PingRules.TryMode(defaults.Mode, out var d) ? d : MarkerMode.Relevant;
		var categories = (off ?? (IReadOnlyCollection<string>)defaults.Off ?? Array.Empty<string>())
			.Select(c => PingRules.TryCategory(c, out var cat) ? cat : (MarkerCategory?)null).Where(c => c.HasValue).Select(c => c.Value).Distinct().ToArray();
		return new MarkerChoice(resolved, categories, !own);
	}

	public static MarkerMode Next(MarkerMode mode) => (MarkerMode)(((int)mode + 1) % 4);

	/// <summary>The server's per-category range in metres; 0 for no limit.</summary>
	public static double MaxRange(MarkerDefaults defaults, MarkerCategory category)
	{
		if (defaults?.MaxRangeMeters == null) return 0;
		foreach (var (key, value) in defaults.MaxRangeMeters)
			if (PingRules.TryCategory(key, out var c) && c == category && double.IsFinite(value) && value > 0) return value;
		return 0;
	}

	public static string Word(MarkerCategory category) => category.ToString().ToLowerInvariant();
}

/// <summary>The chat commands for pings: "/pings all|relevant|pings|off|default" and "/keep".</summary>
public enum PingCommand : byte { None, Usage, Mode, ServerDefaults, Keep }

/// <summary>The client side of pings, apart from the game so it is unit tested.</summary>
public static class PingLogic
{
	public const long HoldMs = 250;
	/// <summary>A grid hit closer than this is a Block ping (what you work on); farther it is the Ship or Base.</summary>
	public const double BlockReachMeters = 25;
	public const double DefaultRangeMeters = 2000;
	public const double KeepAimDegrees = 4;
	public const int CompassTicks = 12;

	public static readonly PingKind[] Radial = { PingKind.Go, PingKind.Danger, PingKind.Mine, PingKind.Build, PingKind.Regroup, PingKind.Look };

	public static PingKind AutoKind(PingHit hit, bool staticGrid, double distance) => hit switch
	{
		PingHit.Voxel => PingKind.Rock,
		PingHit.Character => PingKind.Player,
		PingHit.Grid when distance <= BlockReachMeters => PingKind.Block,
		PingHit.Grid => staticGrid ? PingKind.Base : PingKind.Ship,
		_ => PingKind.Point
	};

	public static double FeedAlpha(double ageS)
		=> ageS <= MarkerRelevance.FeedFadeFrom ? 1 : Math.Clamp((MarkerRelevance.FeedFadeTo - ageS) / (MarkerRelevance.FeedFadeTo - MarkerRelevance.FeedFadeFrom), 0, 1);

	/// <summary>The icon colour set: danger is always red; a server broadcast uses the faction set.</summary>
	public static string Variant(PingKind kind, PingRelation relation) => kind == PingKind.Danger ? "danger" : relation switch
	{
		PingRelation.Own => "own",
		PingRelation.Faction or PingRelation.Admin => "faction",
		PingRelation.Ally => "ally",
		_ => "neutral"
	};

	/// <summary>Icon file name under Textures/GUI/Icons/SE2Online/Markers (without .png).</summary>
	public static string IconName(PingKind kind, PingRelation relation, bool dot)
	{
		string variant = Variant(kind, relation);
		if (dot) return "dot_" + variant;
		return kind == PingKind.Danger ? "danger" : PingRules.Glyph(kind) + "_" + variant;
	}

	/// <summary>What a ping is called: its label, or the kind with what it hit ("Ship Miner 2").</summary>
	public static string Thing(PingShow show)
	{
		if (!string.IsNullOrWhiteSpace(show.Label)) return show.Label;
		string kind = PingRules.KindText(show.Kind), what = show.What ?? "";
		return what.Length == 0 || what == kind ? kind : kind + " " + what;
	}

	/// <summary>The name of the marker in the world; the pinger's name only while it is aimed at.</summary>
	public static string WorldName(PingShow show, bool aimed)
	{
		string name = string.IsNullOrWhiteSpace(show.Label) ? PingRules.KindText(show.Kind) : show.Label;
		return aimed && !string.IsNullOrWhiteSpace(show.FromName) ? show.FromName + ": " + name : name;
	}

	public static string FeedText(PingShow show, double distanceMeters)
		=> (string.IsNullOrWhiteSpace(show.FromName) ? "Someone" : show.FromName) + ": " + Thing(show) + " - " + PingRules.DistanceText(distanceMeters);

	/// <summary>The name of a ping kept as a GPS marker.</summary>
	public static string KeepName(PingShow show) => string.IsNullOrWhiteSpace(show.Label) ? Thing(show) : show.Label;

	public static string ModeToast(MarkerMode mode) => "Markers: " + PingRules.ModeText(mode);

	public static (PingCommand Command, MarkerMode Mode) ParseCommand(string text)
	{
		string t = (text ?? "").Trim();
		if (t.Equals("/keep", StringComparison.OrdinalIgnoreCase)) return (PingCommand.Keep, default);
		if (!t.Equals("/pings", StringComparison.OrdinalIgnoreCase) && !t.StartsWith("/pings ", StringComparison.OrdinalIgnoreCase)) return (PingCommand.None, default);
		string arg = t.Length > 6 ? t[6..].Trim() : "";
		if (arg.Equals("default", StringComparison.OrdinalIgnoreCase) || arg.Equals("server", StringComparison.OrdinalIgnoreCase)) return (PingCommand.ServerDefaults, default);
		return PingRules.TryMode(arg, out var mode) ? (PingCommand.Mode, mode) : (PingCommand.Usage, default);
	}

	public const string Usage = "Usage: /pings all|relevant|pings|off (or default for the server's choice); /keep saves the newest ping as a GPS marker.";

	// Directions are world vectors; forward, right and up are the camera's unit axes.
	public static double Dot((double X, double Y, double Z) a, (double X, double Y, double Z) b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

	/// <summary>Degrees between the camera forward and the direction to the marker.</summary>
	public static double AimDegrees((double X, double Y, double Z) forward, (double X, double Y, double Z) to)
	{
		double length = Math.Sqrt(Dot(to, to));
		if (length < 1e-6) return 0;
		return Math.Acos(Math.Clamp(Dot(forward, to) / length, -1, 1)) * 180 / Math.PI;
	}

	/// <summary>Bearing of the marker left (negative) or right (positive) of the camera forward, -180 to 180 degrees.</summary>
	public static double Bearing((double X, double Y, double Z) forward, (double X, double Y, double Z) right, (double X, double Y, double Z) to)
		=> Math.Atan2(Dot(to, right), Dot(to, forward)) * 180 / Math.PI;

	/// <summary>Whether the marker is inside the camera's view (vertical field of view in radians).</summary>
	public static bool OnScreen((double X, double Y, double Z) forward, (double X, double Y, double Z) right, (double X, double Y, double Z) up,
		(double X, double Y, double Z) to, double fovY, double aspect)
	{
		double z = Dot(to, forward);
		if (z <= 0.01) return false;
		double tanY = Math.Tan(fovY / 2), tanX = tanY * Math.Max(0.1, aspect);
		return Math.Abs(Dot(to, right) / z) <= tanX && Math.Abs(Dot(to, up) / z) <= tanY;
	}

	/// <summary>X of a compass tick on the 1920-wide strip: the strip spans 120 degrees each side.</summary>
	public static double CompassX(double bearing) => Math.Clamp(960 + bearing / 120 * 930, 24, 1896);

	// ---- the planet compass ----

	/// <summary>Within this many degrees of a pole the strip is hidden; it fades in up to <see cref="PoleFadeTo"/>.</summary>
	public const double PoleHide = 2, PoleFadeTo = 6;

	/// <summary>
	/// The local horizon at the player on a planet: up from the planet centre, north toward the planet's +Y pole (SE2's
	/// planet latitude is the planet-local Y of the surface direction), east = north x up (SE2 is right-handed with
	/// forward -Z, so facing north east is on the right). <see cref="Fade"/> is 0 near a pole, where north is undefined.
	/// </summary>
	public readonly record struct PlanetFrame((double X, double Y, double Z) Up, (double X, double Y, double Z) North, (double X, double Y, double Z) East, double Fade);

	/// <summary>The frame at <paramref name="player"/>, or null in space: farther from the closest planet's centre than its
	/// MaxAltimeterRadius, the flight HUD's own "in space" test.</summary>
	public static PlanetFrame? Frame((double X, double Y, double Z) player, (double X, double Y, double Z) center, (double X, double Y, double Z) polarAxis, double maxAltimeterRadius)
	{
		var radial = Sub(player, center);
		double distance = Length(radial);
		if (!(distance > 1e-3) || !(distance <= maxAltimeterRadius) || !(Length(polarAxis) > 1e-9)) return null;
		var up = Scale(radial, 1 / distance);
		var axis = Normalize(polarAxis);
		double along = Math.Clamp(Dot(axis, up), -1, 1);
		double fromPole = Math.Acos(Math.Abs(along)) * 180 / Math.PI;
		double fade = Math.Clamp((fromPole - PoleHide) / (PoleFadeTo - PoleHide), 0, 1);
		var tangent = Sub(axis, Scale(up, along));
		// At a pole any horizontal direction will do: the strip is hidden there, this only keeps the numbers finite.
		if (Length(tangent) < 1e-9)
		{
			(double X, double Y, double Z) any = Math.Abs(up.X) < 0.9 ? (1, 0, 0) : (0, 0, 1);
			tangent = Sub(any, Scale(up, Dot(any, up)));
		}
		var north = Normalize(tangent);
		return new PlanetFrame(up, north, Cross(north, up), fade);
	}

	/// <summary>Compass heading of a direction on the frame's horizon: 0 north, 90 east, clockwise, 0 to 360.</summary>
	public static double Heading(PlanetFrame frame, (double X, double Y, double Z) direction)
	{
		double degrees = Math.Atan2(Dot(direction, frame.East), Dot(direction, frame.North)) * 180 / Math.PI;
		return double.IsFinite(degrees) ? (degrees + 360) % 360 : 0;
	}

	/// <summary>
	/// The heading the camera faces. Straight up or down the forward vector has no horizontal part and atan2 of what is left
	/// spins; leaning it against the camera's own up (away from the sky) gives the same heading when level and a steady
	/// one at any pitch.
	/// </summary>
	public static double Facing(PlanetFrame frame, (double X, double Y, double Z) forward, (double X, double Y, double Z) cameraUp)
		=> Heading(frame, Sub(forward, Scale(cameraUp, Math.Sign(Dot(forward, frame.Up)))));

	/// <summary>The heading of a marker, or false when it is (nearly) straight above or below: under 1% of its distance is
	/// horizontal, so any heading would be noise.</summary>
	public static bool TryBearing(PlanetFrame frame, (double X, double Y, double Z) to, out double heading)
	{
		heading = 0;
		double length = Length(to);
		var horizontal = Sub(to, Scale(frame.Up, Dot(to, frame.Up)));
		if (!(length > 1e-9) || Length(horizontal) < 0.01 * length) return false;
		heading = Heading(frame, horizontal);
		return true;
	}

	/// <summary>A heading relative to where the camera faces, -180 to 180 (positive to the right).</summary>
	public static double Relative(double heading, double facing) => ((heading - facing) % 360 + 540) % 360 - 180;

	/// <summary>
	/// A screen-edge pip in 1920x1080 units for a marker off screen: where the direction from the screen centre meets the
	/// edge (inset), and the pip's rotation in degrees (clockwise; 0 is the shipped pip pointing down). Camera-space x right,
	/// y up, z forward; behind the camera the same side still wins, like the native off-screen arrow.
	/// </summary>
	public static (double X, double Y, double Angle) EdgePip(double x, double y, double z, double inset = 30)
	{
		double dx = x, dy = -y;
		double length = Math.Sqrt(dx * dx + dy * dy);
		if (!(length > 1e-9)) { dx = 0; dy = 1; length = 1; }
		dx /= length; dy /= length;
		double halfW = 960 - inset, halfH = 540 - inset;
		double t = Math.Min(Math.Abs(dx) > 1e-9 ? halfW / Math.Abs(dx) : double.MaxValue, Math.Abs(dy) > 1e-9 ? halfH / Math.Abs(dy) : double.MaxValue);
		double angle = Math.Atan2(dy, dx) * 180 / Math.PI - 90;
		return (960 + dx * t, 540 + dy * t, (angle + 360) % 360);
	}

	private static (double X, double Y, double Z) Sub((double X, double Y, double Z) a, (double X, double Y, double Z) b) => (a.X - b.X, a.Y - b.Y, a.Z - b.Z);
	private static (double X, double Y, double Z) Scale((double X, double Y, double Z) a, double k) => (a.X * k, a.Y * k, a.Z * k);
	private static double Length((double X, double Y, double Z) a) => Math.Sqrt(Dot(a, a));
	private static (double X, double Y, double Z) Normalize((double X, double Y, double Z) a) => Scale(a, 1 / Length(a));
	public static (double X, double Y, double Z) Cross((double X, double Y, double Z) a, (double X, double Y, double Z) b)
		=> (a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

	/// <summary>Colour alpha byte for a marker opacity.</summary>
	public static byte AlphaByte(double alpha) => (byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255);
}
