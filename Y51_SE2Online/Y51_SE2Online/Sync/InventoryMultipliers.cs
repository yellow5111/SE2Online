using System;
using System.Collections.Generic;
using System.Globalization;

namespace Y51_SE2Online.Sync;

/// <summary>
/// Host rule: how much the player inventory and cargo containers hold, as multiples of the game's capacity (mass).
/// Only the hosting game applies it (SyncEngine.InventoryMultiplierRules); players get the new capacity through the
/// replicated inventory data.
/// </summary>
public readonly record struct InventoryMultipliers(double Player, double Cargo)
{
	public const double Min = 0.1, Max = 10;

	public static readonly InventoryMultipliers Default = new(1, 1);

	/// <summary>Set by the hosting session; Default whenever this game is not hosting.</summary>
	public static InventoryMultipliers Current { get; set; } = Default;

	public bool IsDefault => this == Default;

	public static string? Validate(double player, double cargo)
	{
		foreach (var (name, value) in new[] { ("Player inventory", player), ("Cargo container", cargo) })
			if (double.IsNaN(value) || value < Min || value > Max) return $"{name} size multiplier must be between {Min} and {Max}.";
		return null;
	}

	// The character's main inventory (not its consumable or datapad slots).
	public static readonly HashSet<Guid> PlayerInventories = new() { new("7f3d115d-b1bf-4eaa-b9f6-f146a29bdda4") };

	// Storage blocks: cargo containers 150/250/750, crate, locker, barrel.
	public static readonly HashSet<Guid> CargoInventories = new()
	{
		new("cdee1027-47fd-4dd6-a3e9-e7db41e1a811"), new("e08cda9f-6ec7-4be3-bed0-8dd28e3197c6"), new("e647212a-2b71-45c3-ad82-8ffed6889b04"),
		new("81a5c2bd-48ee-4085-867a-2607edd05da7"), new("9ace5180-1d58-42c1-83a8-1000b5bf6984"), new("bf591278-6435-4b80-9ba1-cc62fc6d7a37"),
	};

	/// <summary>The multiplier for an inventory definition; 1 for inventories this rule leaves alone.</summary>
	public double For(Guid inventory) => PlayerInventories.Contains(inventory) ? Player : CargoInventories.Contains(inventory) ? Cargo : 1;

	public string Describe() => "player inventory " + ResourceMultipliers.Format(Player) + ", cargo containers " + ResourceMultipliers.Format(Cargo);
}
