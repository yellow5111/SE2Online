using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Keen.Game2.Client.GameSystems.GPS;
using Keen.Game2.Client.GameSystems.PlayerControl;
using Keen.Game2.Simulation.GameSystems.GPS;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Collections;
using Keen.VRage.Library.Mathematics;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

internal sealed class NametagOverlay
{
	private sealed class Tag
	{
		public GPSMarker Marker;

		public string Name;

		public Entity? Target;
	}

	public static bool Enabled = true;

	public static double Range = 200.0;

	public const double HeadOffset = 2.1;

	public const string MarkerIdentifier = "Y51_SE2Online.Nametag";

	private static readonly FieldInfo EntityBackingField = EngineMembers.Field(HookTargets.Reflected.Single(m => m.Id == "component-entity"));

	private static readonly FieldInfo GpsConfigField = EngineMembers.Field(HookTargets.Reflected.Single(m => m.Id == "gps-render-config"));

	private readonly IMpLog _log;

	private readonly string _side;

	private readonly Dictionary<ushort, Tag> _tags = new();

	private volatile Tag[] _active = Array.Empty<Tag>();

	private static readonly List<WeakReference<NametagOverlay>> Overlays = new();

	public static long DrawRefreshes;

	private static int _drawFailed;

	private Entity? _sessionEntity;

	private NametagMarkerProvider? _provider;

	private GPSMarkerTypeDefinition? _type;

	private object? _styleConfig;

	private bool _failed;

	private string _lastShown = "";

	public long Updates;

	public NametagOverlay(IMpLog log, string side)
	{
		_log = log;
		_side = side;
		lock (Overlays)
		{
			Overlays.RemoveAll(w => !w.TryGetTarget(out _));
			Overlays.Add(new WeakReference<NametagOverlay>(this));
		}
	}

	public static void DrawPrefix(GPSMarkerRenderSessionComponent __instance)
	{
		try
		{
			MarkerColors.ApplyToAntennas(__instance);
		}
		catch (Exception)
		{
		}
		if (!Enabled || _drawFailed != 0)
		{
			return;
		}
		try
		{
			lock (Overlays)
			{
				foreach (WeakReference<NametagOverlay> overlay in Overlays)
				{
					if (overlay.TryGetTarget(out var target) && !target._failed)
					{
						target.PlacePositions();
					}
				}
			}
			DrawRefreshes++;
		}
		catch (Exception)
		{
			_drawFailed = 1;
		}
	}

	private void PlacePositions()
	{
		foreach (Tag tag in _active)
		{
			Entity target = tag.Target;
			if (target != null && !EngineCodec.IsDead(target))
			{
				Vector3D head = HeadPosition(target);
				if (tag.Marker.Position != head)
				{
					tag.Marker.Position = head;
				}
			}
		}
	}

	private static Vector3D HeadPosition(Entity e)
	{
		WorldTransform transform = e.Data.GetWorldTransform();
		Vector3 up = Quaternion.GetUp(transform.Orientation);
		return transform.Position + new Vector3D(up.X, up.Y, up.Z) * HeadOffset;
	}

	public static void ResetAll()
	{
		lock (Overlays)
		{
			foreach (var entry in Overlays)
			{
				if (!entry.TryGetTarget(out var overlay)) continue;
				overlay._active = Array.Empty<Tag>();
				if (overlay._provider != null) overlay._provider.Current = new ObservableList<GPSMarker>();
			}
			Overlays.Clear();
		}
	}

	public static void Configure(MpOptions o)
	{
		Enabled = o.Nametags;
		Range = o.NametagRange;
	}

	/// <summary>The other players this client last saw (for the look-at player options), with their session.</summary>
	internal static (Session Session, IReadOnlyList<(ushort ClientId, string Name, Entity? Target)> Players) Last;

	public void Update(Session session, Entity sessionEntity, IReadOnlyList<(ushort ClientId, string Name, Entity? Target)> players)
	{
		Last = (session, players.Any(p => PlayerTag.IsOffline(p.ClientId)) ? players.Where(p => !PlayerTag.IsOffline(p.ClientId)).ToList() : players);
		if (_failed || !Enabled)
		{
			return;
		}
		try
		{
			UpdateCore(session, sessionEntity, players);
		}
		catch (Exception ex)
		{
			_failed = true;
			_log.Error($"nametags({_side}): failed and switched off for this session (the game is unaffected): {ex}");
			try
			{
				if (_provider != null)
				{
					_provider.Current = new ObservableList<GPSMarker>();
				}
			}
			catch (Exception)
			{
			}
		}
	}

	private void UpdateCore(Session session, Entity sessionEntity, IReadOnlyList<(ushort ClientId, string Name, Entity? Target)> players)
	{
		if (sessionEntity != _sessionEntity)
		{
			if (players.Count == 0)
			{
				return;
			}
			Attach(session, sessionEntity);
			if (_failed)
			{
				return;
			}
		}
		Updates++;
		bool changed = false;
		var seen = new HashSet<ushort>();
		foreach (var (clientId, name, target) in players)
		{
			if (target != null && !EngineCodec.IsDead(target))
			{
				seen.Add(clientId);
				if (!_tags.TryGetValue(clientId, out Tag tag))
				{
					var style = (GPSMarkersConfiguration)_styleConfig;
					var marker = new GPSMarker(_type, name, "Space Engineers 2 Online player", target.Data.GetWorldTransform().Position, PlayerIcon.Get(style.DefaultIcon), new ColorSRGB(140, 210, 255), alwaysShow: true, hidden: false, removeWithEntity: false, MarkerIdentifier, showOnMap: false);
					_tags[clientId] = tag = new Tag
					{
						Marker = marker,
						Name = name
					};
					changed = true;
				}
				else if (tag.Name != name)
				{
					tag.Name = name;
					tag.Marker.Name = name;
				}
				tag.Target = target;
				var color = MarkerColors.For(TeamPermissions.OwnerOf(target)) ?? new ColorSRGB(140, 210, 255);
				tag.Marker.Color = PlayerTag.IsOffline(clientId) ? new ColorSRGB((byte)(color.R * 3 / 5), (byte)(color.G * 3 / 5), (byte)(color.B * 3 / 5), color.A) : color;
			}
		}
		foreach (ushort gone in _tags.Keys.Where(k => !seen.Contains(k)).ToList())
		{
			_tags.Remove(gone);
			changed = true;
		}
		if (changed)
		{
			var markers = new ObservableList<GPSMarker>();
			foreach (Tag tag in _tags.Values)
			{
				markers.Add(tag.Marker);
			}
			_provider.Current = markers;
			string shown = string.Join(", ", from kv in _tags
				orderby kv.Key
				select $"'{kv.Value.Name}' (ClientId {kv.Key})");
			if (shown != _lastShown)
			{
				_lastShown = shown;
				_log.Info($"nametags({_side}): showing {_tags.Count} tag(s){(_tags.Count > 0 ? ": " + shown : "")} (within {Range:0} m)");
			}
		}
		if (changed || _active.Length != _tags.Count)
		{
			_active = _tags.Values.ToArray();
		}
		if (_tags.Count == 0)
		{
			return;
		}
		Vector3D? anchor = null;
		try
		{
			PlayerControllerComponent controller = session.SessionComponents.TryGet<ClientPlayersSessionComponent>()?.LocalPlayerController;
			if (controller != null)
			{
				anchor = GPSMarkerHelpers.GetAnchorPosition(controller);
			}
		}
		catch (Exception)
		{
		}
		foreach (Tag tag in _tags.Values)
		{
			Entity target = tag.Target;
			if (target == null || EngineCodec.IsDead(target))
			{
				tag.Marker.Hidden = true;
				continue;
			}
			Vector3D head = HeadPosition(target);
			// CommitFrame is not ordered after Physics.RigidPositionUpdate (seated pilots re-attach there) and races the HUD draw.
			if (_drawFailed != 0 || DrawRefreshes == 0)
			{
				tag.Marker.Position = head;
			}
			bool tooFar = anchor.HasValue && Vector3D.Distance(anchor.GetValueOrDefault(), in head) > Range;
			if (tag.Marker.Hidden != tooFar)
			{
				tag.Marker.Hidden = tooFar;
			}
		}
	}

	private void Attach(Session session, Entity sessionEntity)
	{
		_sessionEntity = sessionEntity;
		_tags.Clear();
		_lastShown = "";
		GPSMarkerRenderSessionComponent renderer = session.SessionComponents.TryGet<GPSMarkerRenderSessionComponent>();
		if (renderer == null)
		{
			_failed = true;
			_log.Warn("nametags(" + _side + "): this client session has no GPSMarkerRenderSessionComponent (HUD markers); nametags off");
			return;
		}
		if (GpsConfigField.GetValue(renderer) is not GPSMarkersConfiguration config)
		{
			_failed = true;
			_log.Warn("nametags(" + _side + "): the HUD marker renderer has no GPSMarkersConfiguration yet; nametags off");
			return;
		}
		_styleConfig = config;
		_type = null;
		// The first full-display marker type, else the player-made one.
		using (HashSet<GPSMarkerTypeDefinition>.Enumerator types = config.FullDisplayMarkerTypes.GetEnumerator())
		{
			if (types.MoveNext())
			{
				_type = types.Current;
			}
		}
		_type ??= config.PlayerMadeMarkerType;
		NametagMarkerProvider existing = sessionEntity.Components.OfType<NametagMarkerProvider>().FirstOrDefault();
		if (existing != null)
		{
			_provider = existing;
		}
		else
		{
			var created = new NametagMarkerProvider();
			// The component is attached by hand, outside the DCS factory, so its Entity back-reference is set directly.
			EntityBackingField.SetValue(created, sessionEntity);
			sessionEntity.Components = sessionEntity.Components.Add(created);
			_provider = created;
		}
		_log.Info($"nametags({_side}): HUD marker provider attached to the client session ({sessionEntity.Components.Length} components; marker type '{_type?.Name ?? "?"}'); tags show other players' names within {Range:0} m");
	}
}
