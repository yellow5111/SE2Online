#if !PULSAR
using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Keen.VRage.Multiplayer.NetworkStories;
using Keen.VRage.Multiplayer.Transport.OutOfProcess;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// Test rig (-y51online:selftest-swapstory, a player on a server): plays a modified client. The first story the server
/// moved to this game is answered, under its id, as another story (SaveSessionComponent.RequestAutosaveAsync) instead of
/// its own completion, on either transport. The server must refuse it ("access: refused ClientId N's answer to ...: it
/// came back as another story"). "selftest: swap: ..." lines.
/// </summary>
internal static class SwapStoryProbe
{
	private static bool _done;

	public static void Install(Harmony harmony)
	{
		if (!MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "selftest-swapstory")) return;
		harmony.Patch(AccessTools.Method(typeof(OutOfProcessPeerTransport), "TransformStoryInternal", new[] { typeof(StoryProgress), typeof(StoryId) }),
			postfix: new HarmonyMethod(typeof(SwapStoryProbe), nameof(AnswerPostfix)));
		harmony.Patch(AccessTools.Method(typeof(Engine.Y51PeerTransport), "ToStoryData"), postfix: new HarmonyMethod(typeof(SwapStoryProbe), nameof(AnswerPostfix)));
	}

	private static void AnswerPostfix(StoryId id, ref IStoryData __result)
	{
		if (_done || !id.IsFromServer || MpPlugin.App?.ServerSession != null) return;
		var other = typeof(Keen.Game2.Simulation.RuntimeSystems.Saves.SaveSessionComponent).GetNestedTypes(System.Reflection.BindingFlags.NonPublic)
			.FirstOrDefault(t => t.Name.StartsWith("<RequestAutosaveAsync>", StringComparison.Ordinal));
		if (other == null) return;
		_done = true;
		string was = __result is StateMachineEnvelope e ? e.StateMachineTypeName.Split(',')[0] : __result.GetType().Name;
		GameLog.Instance.Info($"selftest: swap: answering the server's story {id} ({was}) as {other.FullName}; the server must refuse it");
		__result = new StateMachineEnvelope { StoryId = id, StateMachineTypeName = other.AssemblyQualifiedName!, State = 0, Fields = new List<StoryField>() };
	}
}
#endif
