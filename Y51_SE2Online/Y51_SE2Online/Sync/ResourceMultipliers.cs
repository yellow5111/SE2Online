using System;
using System.Globalization;

namespace Y51_SE2Online.Sync;

/// <summary>
/// Host rule: how fast suits and blocks use energy, hydrogen and oxygen. 1 is the game's own rate, 0 uses none.
/// Only the hosting game applies it (SyncEngine.ResourceMultiplierRules); joiners see the effect through the
/// replicated stats and resource flows.
/// </summary>
public readonly record struct ResourceMultipliers(double Energy, double Hydrogen, double Oxygen)
{
	public const double Max = 10;

	public static readonly ResourceMultipliers Default = new(1, 1, 1);

	/// <summary>Set by the hosting session; Default whenever this game is not hosting.</summary>
	public static ResourceMultipliers Current { get; set; } = Default;

	public bool IsDefault => this == Default;

	public static string? Validate(double energy, double hydrogen, double oxygen)
	{
		foreach (var (name, value) in new[] { ("Energy", energy), ("Hydrogen", hydrogen), ("Oxygen", oxygen) })
			if (double.IsNaN(value) || value < 0 || value > Max) return $"{name} usage multiplier must be between 0 and {Max}.";
		return null;
	}

	/// <summary>The multiplier for a grid resource type definition (electricity, hydrogen, oxygen); 1 for anything else.</summary>
	public double ForResource(Guid resourceType) =>
		resourceType == ElectricityResource ? Energy : resourceType == HydrogenResource ? Hydrogen : resourceType == OxygenResource ? Oxygen : 1;

	public static readonly Guid ElectricityResource = new("bcded093-f5c0-4997-af3a-a6fbd853ad66");
	public static readonly Guid HydrogenResource = new("a0e12811-7f81-4735-aef4-b74ef0c2d395");
	public static readonly Guid OxygenResource = new("d129d38e-116d-4424-a1a8-bbb298ac9dba");

	/// <summary>The multiplier for a character stat definition (suit energy, hydrogen, oxygen); 1 for anything else.</summary>
	public double ForStat(Guid stat) =>
		stat == EnergyStat ? Energy : stat == HydrogenStat ? Hydrogen : stat == OxygenStat ? Oxygen : 1;

	public static readonly Guid EnergyStat = new("675206d7-7a7a-445c-946c-e0a74bbcd862");
	public static readonly Guid HydrogenStat = new("fec470a5-43de-4c0d-a712-1bcffd731373");
	public static readonly Guid OxygenStat = new("c162f32f-75ab-4d17-8a49-696dc6caf1ff");

	public static string Format(double value) => "x" + value.ToString("0.0#", CultureInfo.InvariantCulture);

	public string Describe() => $"energy {Format(Energy)}, hydrogen {Format(Hydrogen)}, oxygen {Format(Oxygen)}";

	/// <summary>Console form: "energy 2", "hydrogen 0.5", "oxygen 0".</summary>
	public bool TryWith(string name, string value, out ResourceMultipliers result, out string error)
	{
		result = this;
		if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) { error = "Not a number: " + value; return false; }
		switch (name.ToLowerInvariant())
		{
			case "energy" or "power": result = this with { Energy = v }; break;
			case "hydrogen" or "h2": result = this with { Hydrogen = v }; break;
			case "oxygen" or "o2": result = this with { Oxygen = v }; break;
			default: error = "Use energy, hydrogen or oxygen."; return false;
		}
		error = Validate(result.Energy, result.Hydrogen, result.Oxygen) ?? "";
		if (error.Length > 0) result = this;
		return error.Length == 0;
	}
}
