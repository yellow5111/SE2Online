using System;
using System.Collections.Generic;
using System.Linq;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.Engine;

/// <summary>
/// Entity data the server re-sends every frame although players only see it as ambience: these go to each player at most
/// at the rate below (the newest value always arrives). Anything not listed goes as often as the server changes it.
/// -y51online:datarates:off sends everything at the server's rate.
/// </summary>
internal static class DataRates
{
	private static readonly Dictionary<string, int> Ms = new()
	{
		// Wind and weather around each wind source and planet region: trees, particles, sound.
		["Keen.Game2.Simulation.GameSystems.RangedAffectGenerators.Wind.WindGeneratorData"] = 250,
		["Keen.Game2.Simulation.GameSystems.RangedAffectGenerators.Weather.WeatherResourceData"] = 500,
		["Keen.Game2.Simulation.GameSystems.RangedAffectGenerators.Atmosphere.AirData"] = 500,
		["Keen.Game2.Simulation.WorldObjects.CubeBlocks.Production.WindTurbineComponent+WindTurbineDynamicData"] = 250,
	};

	// Values a moving body re-sends every frame (the gravity it is in, wheel dust and sound): at full rate to a player within
	// 250 m of it (their own ship, what they stand next to), 4 times a second beyond, where the body itself is played back
	// from motion samples.
	private static readonly HashSet<string> NearOnly = new()
	{
		"Keen.VRage.Core.Game.GameSystems.Gravity.GravityEffectData",
		"Keen.VRage.Physics.Data.CustomGravity",
		"Keen.Game2.Simulation.WorldObjects.CubeBlocks.MechanicalBlocks.Wheels.WheelCornerModuleEffectData",
	};

	public const int FarMs = 250;

	public static bool ByDistance(Type type) => !Off && NearOnly.Contains(type.FullName ?? "");

	private static readonly bool Off = MpPlugin.LaunchArgs.Contains(MpOptions.Prefix + "datarates:off");

	public static int? MinIntervalMs(Type type) => !Off && Ms.TryGetValue(type.FullName ?? "", out int ms) ? ms : null;
}
