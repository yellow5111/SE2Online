#if !PULSAR
using System;
using System.Linq;
using HarmonyLib;
using Keen.VRage.Multiplayer.NetworkStories;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// Test rig (-y51online:selftest-swapstory, a player on a server): plays a modified client. The first story the server
/// moved to this game is answered, under its id, as another story (SaveSessionComponent.RequestAutosaveAsync) instead of
/// its own completion. The server must refuse it ("access: refused ClientId N's answer to ...: it came back as another
/// story"). "selftest: swap: ..." lines.
/// </summary>
internal static class SwapStoryProbe
{
	private static bool _done;

	public static void Install(Harmony harmony)
	{
		if (!MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "selftest-swapstory")) return;
		harmony.Patch(AccessTools.Method(typeof(Engine.Y51PeerTransport), "ToStoryOut"), postfix: new HarmonyMethod(typeof(SwapStoryProbe), nameof(AnswerPostfix)));
	}

	private static void AnswerPostfix(StoryId id, object __result)
	{
		if (_done || !id.IsFromServer || MpPlugin.App?.ServerSession != null) return;
		var other = typeof(Keen.Game2.Simulation.RuntimeSystems.Saves.SaveSessionComponent).GetNestedTypes(System.Reflection.BindingFlags.NonPublic)
			.FirstOrDefault(t => t.Name.StartsWith("<RequestAutosaveAsync>", StringComparison.Ordinal));
		if (other == null) return;
		var traverse = Traverse.Create(__result);
		if (traverse.Field("Completion").GetValue() == null) return;
		_done = true;
		GameLog.Instance.Info($"selftest: swap: answering the server's story {id} as {other.FullName}; the server must refuse it");
		traverse.Field("Completion").SetValue(null);
		traverse.Field("Move").SetValue(new Engine.OwnStoryMove { Id = id, TypeName = other.AssemblyQualifiedName!, State = 0 });
	}
}
#endif
