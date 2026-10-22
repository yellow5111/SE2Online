using System.Collections.Generic;
using System.Text.Json;
using HarmonyLib;

namespace Y51_SE2Online.Dedicated;

/// <summary>Definition overrides are applied with the session settings (NativeSessionSettings, CustomRulesHost).</summary>
internal static class NativeDefinitionSettings
{
	internal static void Validate(Dictionary<string, Dictionary<string, JsonElement>> settings) => NativeSessionSettings.Validate(settings);

	internal static void Install(Harmony harmony) { }

	internal static void Finish() { }
}
