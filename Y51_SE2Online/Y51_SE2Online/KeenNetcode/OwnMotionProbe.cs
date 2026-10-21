#if !PULSAR
using System.Linq;
using HarmonyLib;
using Keen.Game2.Client.GameSystems.PlayerControl;
using Keen.Game2.Simulation.WorldObjects.Characters;
using Keen.Game2.Simulation.WorldObjects.Movement;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Accessors;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Mathematics;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// Test rig only (-y51online:selftest-ownmotion): can a joined client move its own character itself, as 2.4 did? The
/// server's motion samples stop moving this player's character, the client holds forward for 3 s, and its position is
/// logged every frame step, so the log shows whether the client's own simulation moves it and how soon.
/// </summary>
internal static class OwnMotionProbe
{
	private static bool _on;
	private static DEntity _own;
	private static bool _hasOwn;
	private static long _driveAtMs, _logUntilMs;
	private static Vector3D _last;

	public static void Install(Harmony harmony)
	{
		_on = MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "selftest-ownmotion");
		if (!_on) return;
		if (MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "selftest-ownmotion-keep")) { GameLog.Instance.Info("selftest: own motion: server samples still move the character (control run)"); return; }
		var system = AccessTools.TypeByName("Keen.VRage.Game.Multiplayer.MotionStateApplicationSystem");
		harmony.Patch(AccessTools.Method(system, "EvaluateMotionState"), prefix: new HarmonyMethod(typeof(OwnMotionProbe), nameof(EvaluatePrefix)));
		GameLog.Instance.Info("selftest: own motion: server motion samples no longer move this player's own character");
	}

	private static bool EvaluatePrefix(DEntity entity) => !(_hasOwn && entity == _own);

	public static void Tick(Keen.Game2.GameAppComponent app)
	{
		if (!_on || app.ClientSession is not { } session || app._isTransitioning.IsSet) return;
		var controller = session.SessionComponents.TryGet<ClientPlayersSessionComponent>()?.LocalPlayerController;
		var own = controller?.ControlledEntities.Select(c => (c.Controllable as Component)?.Entity).FirstOrDefault(e => e != null && e.Data.TryGet<ControlData>(out _));
		if (own == null) { _hasOwn = false; return; }
		_own = own.DEntity;
		_hasOwn = true;
		long now = MpClock.NowMs();
		var p = own.Data.GetWorldTransform().Position;
		if (_driveAtMs == 0)
		{
			_driveAtMs = now + 20000;
			_last = p;
			GameLog.Instance.Info($"selftest: own motion: body found at {p.X:0.00}, {p.Y:0.00}, {p.Z:0.00}; forward in 20 s");
			return;
		}
		bool driving = now >= _driveAtMs && now < _driveAtMs + 3000;
		if (driving) own.Data.Set(new ControlData { Movement = new Vector3(0, 0, -1) });
		else if (now >= _driveAtMs + 3000 && now < _driveAtMs + 3100) own.Data.Set(default(ControlData));
		if (now >= _driveAtMs - 200 && now < _driveAtMs + 4000)
		{
			double step = (p - _last).Length();
			GameLog.Instance.Info($"selftest: own motion t={now - _driveAtMs} ms {(driving ? "fwd" : "idle")} at {p.X:0.00}, {p.Y:0.00}, {p.Z:0.00} step {step * 1000:0} mm");
		}
		_last = p;
	}
}
#endif
