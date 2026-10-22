using System;
using System.Collections.Generic;
using HarmonyLib;
using Keen.Game2.Simulation.WorldObjects.Characters;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.DCS.Components;
using Keen.VRage.Game.Multiplayer;
using Keen.VRage.Multiplayer.Sessions;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.Engine;

/// <summary>
/// The game stops replicating a character's motion when it dies (NoAutoSync) and lets every game simulate its own corpse,
/// so a body that slid or was pushed on the server lay metres away on each player's screen, and a late player saw it
/// where it died. While a corpse moves the server sends its transform to every player on SE2 Online's transport (at most
/// 10 times a second, only after it moved), and a resting one once to each player that has not had it.
/// </summary>
internal static class CorpseMotion
{
	private const long IntervalMs = 100;
	private const double MoveMetres = 0.02;

	private static readonly List<Entity> Corpses = new();
	private static readonly Dictionary<(Y51ServerTransport, Entity), (WorldTransform At, long Ms, bool Resting)> Sent = new();
	private static long _nextMs;
	internal static TimeSpan LatestServerTime;
	public static long Samples;

	public static void Install(Harmony harmony)
		=> harmony.Patch(AccessTools.Method(typeof(CharacterDeathServerComponent), "OnCharacterDeathSignal"), postfix: new HarmonyMethod(typeof(CorpseMotion), nameof(DeathPostfix)));

	private static void DeathPostfix(CharacterDeathServerComponent __instance)
	{
		lock (Corpses) Corpses.Add(__instance.Entity);
	}

	/// <summary>Server session thread, after its update.</summary>
	public static void Tick(Keen.VRage.Core.Game.Systems.Session server)
	{
		long now = MpClock.NowMs();
		if (now < _nextMs) return;
		_nextMs = now + IntervalMs;
		lock (Corpses)
		{
			if (Corpses.Count == 0) return;
			for (int i = Corpses.Count - 1; i >= 0; i--)
			{
				var corpse = Corpses[i];
				if (!server.Scene.IsEntityAlive(corpse.DEntity))
				{
					Corpses.RemoveAt(i);
					continue;
				}
				var at = corpse.Data.GetWorldTransform();
				foreach (var transport in Y51Replication.ServerTransports)
				{
					var key = (transport, corpse);
					bool known = Sent.TryGetValue(key, out var last);
					bool moved = !known || (at.Position - last.At.Position).Length() > MoveMetres || Math.Abs(Dot(at, last.At)) < 0.9999f;
					if (moved) Send(server.Scene, transport, corpse, at, resting: false, now);
					// Still for a second after moving: one reliable resting state, so nothing is left on a lost sample.
					else if (!last.Resting && now - last.Ms >= 1000) Send(server.Scene, transport, corpse, at, resting: true, now);
				}
			}
			if (Sent.Count > 4 * Math.Max(1, Corpses.Count) * 8)
			{
				var gone = new List<(Y51ServerTransport, Entity)>();
				foreach (var key in Sent.Keys)
					if (!Corpses.Contains(key.Item2) || key.Item1.Disposed) gone.Add(key);
				foreach (var key in gone) Sent.Remove(key);
			}
		}
	}

	private static float Dot(in WorldTransform a, in WorldTransform b)
		=> a.Orientation.X * b.Orientation.X + a.Orientation.Y * b.Orientation.Y + a.Orientation.Z * b.Orientation.Z + a.Orientation.W * b.Orientation.W;

	// What ServerSessionComponent.SyncMotionState builds for a live body: a sample without its motion parent's relative
	// transform or its body read on the owner's game as a world-space body with no velocity and drifted off.
	private static void Send(Keen.VRage.DCS.Scenes.Scene scene, Y51ServerTransport transport, Entity corpse, WorldTransform at, bool resting, long now)
	{
		var state = new EntityMotionState { ServerTime = LatestServerTime, Transform = at, Flags = resting ? MotionStateFlags.Resting : 0 };
		if (corpse.Data.TryGet<MotionParent>(out var parent))
		{
			ref var parentAt = ref Keen.VRage.DCS.Accessors.DataMarshal.TryGetDataPointer<WorldTransform>(scene, parent.Parent, writeAccess: false);
			if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref parentAt))
			{
				state.Relative = (RelativeTransform)WorldTransform.GetRelativeTransform(at, parentAt);
				state.ParentEpoch = parent.Epoch;
			}
		}
		if (corpse.Data.TryGet<Keen.VRage.Physics.Data.RigidBodyData>(out var body) && !corpse.Data.Has<Keen.VRage.Physics.Data.TransformDriven>())
		{
			state.Body = body;
			state.Flags |= MotionStateFlags.HasBody;
		}
		// The body stops crouching as it dies; with replication off for it, a player's game kept the crouch tag.
		if (!corpse.Data.Has<CharacterCrouchTag>()) transport.RemoveEntityData<CharacterCrouchTag>(corpse);
		transport.SendSampledEntityData(corpse, in state, resting ? SendMode.Reliable : SendMode.Unreliable);
		Sent[(transport, corpse)] = (at, now, resting);
		Samples++;
	}
}
