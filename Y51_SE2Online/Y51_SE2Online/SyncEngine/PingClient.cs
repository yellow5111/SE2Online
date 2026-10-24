using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Keen.Game2.Client.GameSystems.GPS;
using Keen.Game2.Client.GameSystems.PlayerControl;
using Keen.Game2.Client.UI.InGame;
using Keen.Game2.Client.UI.Library;
using Keen.Game2.Client.UI.Library.Controls.QuickMenu;
using Keen.Game2.Client.UI.Library.Controls.RadialMenu;
using Keen.Game2.Client.UI.Library.Dialogs.TextInputDialog;
using Keen.Game2.Client.UI.Library.Dialogs.TwoOptionsDialog;
using Keen.Game2.Simulation.GameSystems.GPS;
using Keen.Game2.Simulation.WorldObjects.Characters;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.Game2.Simulation.WorldObjects.CubeGrids.Physicss;
using Keen.VRage.Audio;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.Core.Game.GameSystems.Audio;
using Keen.VRage.Core.Game.GameSystems.Queries;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Collections;
using Keen.VRage.Library.Definitions;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Library.Memory;
using Keen.VRage.Library.Utils;
using Keen.VRage.Physics;
using Keen.VRage.Physics.Data;
using Keen.VRage.Physics.Queries;
using Keen.VRage.Voxels.Components;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// Design 10.1, client side. The ping key: a tap pings what the crosshair is on, holding it opens SE2's own radial quick
/// menu (Go, Danger, Mine, Build, Regroup, Look, Text), Ctrl with it keeps the ping under the crosshair as a GPS marker.
/// Pings from the host (<see cref="PingInbox"/>) become markers through <see cref="PingMarkers"/>, the compass strip and
/// the feed through <see cref="PingHud"/>.
/// </summary>
internal static class PingClient
{
	// The quick menu definition SE2's toolbar and paint tool radials use (EmoteMenu opens the same one).
	private static readonly Guid QuickMenuId = new("183da096-9d49-4d8c-9954-7c00153ed90e");
	private const string CreateSound = "event:/UI/gps_create", ShareSound = "event:/UI/gps_share", GenericSound = "event:/UI/gps_generic";
	private const long SecondBeepMs = 220;

	private static readonly PingBook Book = new();
	private static readonly HoldGesture Gesture = new();
	private static readonly Dictionary<BaseRadialMenuPanel, PingKind?> Panels = new();
	private static IObservableDisposable _radial;
	private static volatile BaseRadialMenuPanel _hovered;
	private static volatile bool _chosen;
	private static bool _ctrlAtPress, _active, _pressed;
	private static long _nextMarkersMs, _secondBeepAtMs = -1;
	private static SharedUIComponent _shared;
	private static long _nextSharedFindMs;

	internal static void Install(Harmony harmony)
	{
		harmony.Patch(EngineMembers.Method(HookTargets.GameUpdate), postfix: new HarmonyMethod(typeof(PingClient), nameof(GameUpdatePostfix)));
		try { MarkerFilter.Install(harmony); }
		catch (Exception ex) { GameLog.Instance.Warn("markers: the vanilla marker filter is not installed, vanilla markers always show (" + ex.GetType().Name + ": " + ex.Message + ")"); }
	}

	private static void GameUpdatePostfix(Keen.Game2.GameAppComponent __instance)
	{
		try { Tick(__instance); }
		catch (Exception ex) { GameLog.Instance.WarnOnce("ping-tick", "pings: client update failed: " + ex); }
	}

	private static SharedUIComponent Shared(Keen.Game2.GameAppComponent app)
	{
		if (_shared == null && MpClock.NowMs() >= _nextSharedFindMs) { _nextSharedFindMs = MpClock.NowMs() + 2000; _shared = JoinStatusUi.FindShared(app); }
		return _shared;
	}

	private static void Tick(Keen.Game2.GameAppComponent app)
	{
		long now = MpClock.NowMs();
		var shared = Shared(app);
		bool inWorld = AdminAccess.InSession && app.Client != null && app.ClientSession != null && !app._isTransitioning.IsSet && shared != null;
		if (!inWorld)
		{
			if (_active) Stop();
			PingHud.Tick(shared, false);
			return;
		}
		if (!_active)
		{
			_active = true;
			// Pings that came in while the world loaded are stale and would all sound at once.
			PingInbox.Clear();
			MarkerFilter.Refresh(now, force: true);
		}
		MarkerFilter.Refresh(now);
		Drain(now);
		bool free = shared.TryGetTopScreenNeedingInput() == null && !ChatUi.InputOpen;
		PingKey(app, shared, now, free || _radial != null);
		MarkerFilter.Keys(shared, now, free || MarkerFilter.PanelOpen);
		if (now >= _nextMarkersMs)
		{
			_nextMarkersMs = now + 100;
			Book.Expire(now);
			PingMarkers.Update(app.ClientSession, Book, MarkerFilter.Choice, now);
		}
		if (_secondBeepAtMs >= 0 && now >= _secondBeepAtMs) { _secondBeepAtMs = -1; Play(GenericSound); }
		PingHud.Tick(shared, HudVisible(app));
	}

	// The native marker renderer draws nothing while the game hides its HUD; the strip and the feed follow it.
	private static bool HudVisible(Keen.Game2.GameAppComponent app)
	{
		try
		{
			var renderer = app.ClientSession?.SessionComponents.TryGet<GPSMarkerRenderSessionComponent>();
			return renderer == null || renderer.Enabled && renderer._uiEngineComponent?.ScreenManager?.UIVisible != false;
		}
		catch (Exception) { return true; }
	}

	private static void Stop()
	{
		_active = false;
		CloseRadial(false);
		Gesture.Reset();
		Book.Clear();
		PingInbox.Clear();
		PingMarkers.Reset();
		MarkerFilter.Deactivate();
		_shared = null;
	}

	private static void Drain(long now)
	{
		int n = 0;
		bool share = false, danger = false;
		while (n++ < 64 && PingInbox.TryTake(out var show))
		{
			var entry = Book.Add(show, now);
			if (entry == null || show.Relation == PingRelation.Own) continue;
			if (show.Kind == PingKind.Danger) danger = true;
			else share = true;
		}
		// One sound per frame however many pings arrived.
		if (danger) { Play(GenericSound); _secondBeepAtMs = now + SecondBeepMs; }
		else if (share) Play(ShareSound);
	}

	// ---- the ping key ----

	private static void PingKey(Keen.Game2.GameAppComponent app, SharedUIComponent shared, long now, bool usable)
	{
		bool plain = false, withCtrl = false;
		if (usable && KeyBinding.TryParse(MpMenu.Settings.PingKey, out var key) && GameKeys.OwnWindowInFront())
		{
			plain = key.IsDown(GameKeys.IsDown);
			withCtrl = !key.Ctrl && (key with { Ctrl = true }).IsDown(GameKeys.IsDown);
		}
		bool down = plain || withCtrl;
		if (down && !Gesture.Held && _radial == null && !_pressed) _ctrlAtPress = withCtrl;
		_pressed = down;
		switch (Gesture.Update(down, now))
		{
		case HoldGesture.Edge.Tap:
			if (_ctrlAtPress) KeepAimed();
			else Ping(app, null, "");
			break;
		case HoldGesture.Edge.HoldStart:
			if (!_ctrlAtPress) OpenRadial(app);
			break;
		case HoldGesture.Edge.HoldEnd:
			CloseRadial(true);
			break;
		}
		if (!usable && _radial != null) CloseRadial(false);
	}

	private static void OpenRadial(Keen.Game2.GameAppComponent app)
	{
		try
		{
			var ui = app.ClientSession?.SessionComponents.TryGet<SessionInGameUISessionComponent>();
			if (ui == null || !Singleton<DefinitionManager>.Instance.TryGetDefinition(QuickMenuId, out QuickMenuDefinition definition)) return;
			Panels.Clear();
			var slices = new MergeableList<BaseRadialMenuPanel>();
			foreach (var kind in PingLogic.Radial) slices.Add(Slice(PingRules.KindText(kind), kind));
			slices.Add(Slice("Text", null));
			_hovered = null;
			var menu = new QuickMenuScreenViewModel(definition, new QuickMenuData { RootMenu = new RadialMenuData(slices, new BaseRadialMenuPanel()) },
				selected =>
				{
					if (selected == null || _chosen) return;
					_chosen = true;
					MpMenu.Queue(() => Choose(app, selected));
				}, hovered => _hovered = hovered);
			var radial = ui.OpenQuickMenu(menu);
			_radial = radial;
			// Esc (or the game) closing the menu cancels it: releasing the key then pings nothing.
			if (radial != null) radial.OnDisposed += _ => { if (ReferenceEquals(_radial, radial)) _radial = null; };
		}
		catch (Exception ex)
		{
			_radial = null;
			GameLog.Instance.WarnOnce("ping-radial", "pings: the ping menu failed, a tap still pings: " + ex.Message);
		}
	}

	private static BaseRadialMenuPanel Slice(string label, PingKind? kind)
	{
		var panel = new BaseRadialMenuPanel { DisplayName = label };
		Panels[panel] = kind;
		return panel;
	}

	private static void CloseRadial(bool chooseHovered)
	{
		var open = _radial;
		_radial = null;
		if (open == null) return;
		var hovered = _hovered;
		_hovered = null;
		open.Dispose();
		bool clicked = _chosen;
		_chosen = false;
		var app = MpPlugin.App;
		if (!chooseHovered || clicked || app == null) return;
		// Released in the centre: the automatic kind.
		if (hovered == null) Ping(app, null, "");
		else Choose(app, hovered);
	}

	/// <summary>Game thread.</summary>
	private static void Choose(Keen.Game2.GameAppComponent app, BaseRadialMenuPanel panel)
	{
		if (!Panels.TryGetValue(panel, out var kind)) return;
		if (kind.HasValue) { Ping(app, kind, ""); return; }
		var aim = Aim(app);
		var shared = Shared(app);
		if (aim == null || shared == null) return;
		shared.ShowDialog(new TextInputDialogViewModel(new TwoOptionsDialogDefinition
		{
			Title = OnlineUi.Raw("Ping label"), Content = OnlineUi.Raw("Up to " + PingRules.MaxLabel + " characters, shown with your ping."),
			ConfirmOption = OnlineUi.Raw("Ping"), CancelOption = OnlineUi.Raw("Cancel")
		}, "", focusText: true)
		{
			InputValidator = text => (text ?? "").Length > PingRules.MaxLabel ? "At most " + PingRules.MaxLabel + " characters." : null,
			ConfirmAction = text => MpMenu.Queue(() => Send(aim.Value, null, PingRules.CleanLabel(text)))
		});
	}

	// ---- sending ----

	private static void Ping(Keen.Game2.GameAppComponent app, PingKind? kind, string label)
	{
		var aim = Aim(app);
		if (aim == null) { MarkerFilter.Toast("Nothing to ping from here."); return; }
		Send(aim.Value, kind, label);
	}

	private static void Send((Vector3D At, PingKind Kind, string What) aim, PingKind? chosen, string label)
	{
		var kind = chosen ?? aim.Kind;
		var ping = new PingSend(aim.At.X, aim.At.Y, aim.At.Z, kind, kind, label ?? "", aim.What ?? "");
		if (AdminAccess.Hosting) AdminAccess.Host.LocalPing(ping);
		else AdminAccess.Client?.SendPing(ping);
		if (kind == PingKind.Danger) { Play(GenericSound); _secondBeepAtMs = MpClock.NowMs() + SecondBeepMs; }
		else Play(CreateSound);
	}

	/// <summary>The crosshair's target: a raycast from the camera, past the local character (and the seat's own grid).</summary>
	private static (Vector3D At, PingKind Kind, string What)? Aim(Keen.Game2.GameAppComponent app)
	{
		try
		{
			var session = app.ClientSession;
			var controller = session?.SessionComponents.TryGet<ClientPlayersSessionComponent>()?.LocalPlayerController;
			var camera = controller?.CameraSystem?.RenderCameraEntity;
			if (camera == null) return null;
			var view = camera.Data.GetWorldTransform();
			var forward = (Vector3D)view.Orientation.GetForward();
			double range = AdminAccess.Hosting ? AdminAccess.Host.PingSettings.MaxRangeMeters : AdminAccess.Client?.Admin is { PingMaxRangeMeters: > 0 } admin ? admin.PingMaxRangeMeters : PingLogic.DefaultRangeMeters;
			Entity self = null, seatGrid = null;
			foreach (var controlled in controller.ControlledEntities)
			{
				var e = (controlled.Controllable as Component)?.Entity;
				if (e == null) continue;
				if (e.TryGet<CharacterComponent>() != null) self = e;
				else seatGrid ??= WorldSocial.GridOf(e);
			}
			var physics = camera.GetSession<IPhysics>();
			var scene = camera.Scene;
			if (physics == null || scene == null) return (view.Position + forward * range, PingKind.Point, "");
			using Buffer<SweepQueryHit> hits = default;
			var ray = RayCastArgs.CreateFromTo(view.Position, view.Position + forward * range);
			physics.CastRay(hits, in ray, CollisionPreset.Default);
			double best = double.MaxValue;
			Entity bestBody = null;
			bool bestVoxel = false;
			Vector3D bestAt = default;
			foreach (var hit in hits)
			{
				if (self != null && hit.Body == self.DEntity) continue;
				double distance = Vector3D.Distance(view.Position, hit.Position);
				if (distance >= best || distance < 0.3) continue;
				bool voxel = VoxelStorageComponentBase.IsEntityVoxel((Scene: scene, Entity: hit.Body));
				var body = voxel ? null : Entity.TryGetFromDataEntity((Scene: scene, Entity: hit.Body));
				if (!voxel && body == null) continue;
				if (body != null && (Within(body, self) || seatGrid != null && (body == seatGrid || WorldSocial.GridOf(body) == seatGrid))) continue;
				best = distance; bestBody = body; bestVoxel = voxel; bestAt = hit.Position;
			}
			if (best == double.MaxValue) return (view.Position + forward * range, PingKind.Point, "");
			if (bestVoxel) return (bestAt, PingLogic.AutoKind(PingHit.Voxel, false, best), "");
			if (bestBody.TryGet<CharacterComponent>() != null)
			{
				string name = NametagOverlay.Last.Players?.FirstOrDefault(p => p.Target == bestBody).Name ?? "";
				return (bestAt, PingKind.Player, FactionTag.Strip(name));
			}
			var grid = bestBody.TryGet<CubeGridComponent>() != null || bestBody.TryGet<GridPhysicsComponent>() != null ? bestBody : WorldSocial.GridOf(bestBody);
			if (grid == null) return (bestAt, PingKind.Point, "");
			bool isStatic = grid.TryGet<GridPhysicsComponent>()?.GridMotionType == BodyArgs.Motion.Static;
			string gridName = "";
			try { gridName = grid.TryGet<CubeGridComponent>()?.DisplayName.EvaluateText() ?? ""; } catch (Exception) { }
			return (bestAt, PingLogic.AutoKind(PingHit.Grid, isStatic, best), gridName.Length > PingRules.MaxWhat ? gridName[..PingRules.MaxWhat] : gridName);
		}
		catch (Exception ex)
		{
			GameLog.Instance.WarnOnce("ping-aim", "pings: the aim raycast failed: " + ex.Message);
			return null;
		}
	}

	// The character's own parts and the tool in its hands.
	private static bool Within(Entity body, Entity self)
	{
		for (int depth = 0; body != null && self != null && depth < 8; depth++)
		{
			if (body == self) return true;
			body = body.TryGet<HierarchyComponent>()?.Parent;
		}
		return false;
	}

	// ---- keeping a ping as a GPS marker ----

	private static void KeepAimed()
	{
		var show = PingMarkers.Aimed();
		if (show == null) { MarkerFilter.Toast("Aim at a ping to keep it."); return; }
		Keep(show);
	}

	internal static void Keep(uint id)
	{
		var entry = Book.Live.FirstOrDefault(e => e.Show.Id == id);
		if (entry != null) Keep(entry.Show);
	}

	/// <summary>Sends the ping to this player's own GPS list (GpsShare to self).</summary>
	internal static bool Keep(PingShow show)
	{
		if (show == null) return false;
		ushort self = AdminAccess.Hosting ? SyncProtocol.HostClientId : AdminAccess.Client?.ClientId ?? 0;
		if (self == 0) return false;
		var colour = PingMarkers.ColourOf(show);
		var share = new GpsShare(self, PingLogic.KeepName(show), show.X, show.Y, show.Z, colour.R, colour.G, colour.B, -1);
		if (AdminAccess.Hosting) AdminAccess.Host.LocalGpsShare(share);
		else AdminAccess.Client?.SendGpsShare(share);
		Play(CreateSound);
		return true;
	}

	/// <summary>"/pings all|relevant|pings|off|default" and "/keep"; never sent as chat.</summary>
	internal static bool Command(string text)
	{
		var (command, mode) = PingLogic.ParseCommand(text);
		if (command == PingCommand.None) return false;
		MpMenu.Queue(() => Run(command, mode));
		return true;
	}

	private static void Run(PingCommand command, MarkerMode mode)
	{
		string reply;
		switch (command)
		{
		case PingCommand.Keep:
			var newest = Book.Newest;
			reply = newest == null ? "There is no ping to keep." : Keep(newest.Show) ? "Keeping '" + PingLogic.KeepName(newest.Show) + "' as a GPS marker." : "You are not in a multiplayer world.";
			break;
		case PingCommand.Mode:
			MarkerFilter.SetMode(mode);
			reply = PingLogic.ModeToast(mode) + ".";
			break;
		case PingCommand.ServerDefaults:
			MarkerFilter.UseServerDefaults();
			reply = "Markers follow the server's defaults (" + PingRules.ModeText(MarkerFilter.Choice.Mode) + ").";
			break;
		default:
			reply = PingLogic.Usage + " Now: " + PingRules.ModeText(MarkerFilter.Choice.Mode) + ".";
			break;
		}
		ChatLog.Add(new ChatLine("Pings", 0, reply, ChatKind.System, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), MpClock.NowMs());
	}

	// ---- sounds ----

	private static void Play(string path)
	{
		try
		{
			var camera = MpPlugin.App?.ClientSession?.SessionComponents.TryGet<ClientPlayersSessionComponent>()?.LocalPlayerController?.CameraSystem?.RenderCameraEntity;
			camera?.GetSession<IAudioManager>()?.PlayOneShot(new AudioEvent(path));
		}
		catch (Exception ex) { GameLog.Instance.WarnOnce("ping-sound", "pings: playing " + path + " failed: " + ex.Message); }
	}
}
