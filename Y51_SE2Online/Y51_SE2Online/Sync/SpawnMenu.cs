using System;
using System.Collections.Generic;
using System.Linq;

namespace Y51_SE2Online.Sync;

public enum SpawnKind : byte { Medbay, FactionMedbay, SharedMedbay, Suit, Ship, NearHost, Home }

/// <summary>Why a spawn card cannot be used (Available when it can).</summary>
public enum SpawnStatus : byte { Available, Unpowered, Damaged, TurnedOff, NoAccess, Destroyed, NotAllowed, Cooldown, Unavailable }

/// <param name="Grid">The grid a medbay is on ("" for suit, ships and planet landers).</param>
/// <param name="Distance">Metres from where the player died, -1 when unknown (first spawn).</param>
/// <param name="Place">The planet it is on or lands on, "" for space.</param>
/// <param name="MapX">With <paramref name="HasMap"/>: where it is relative to the death point, scaled into -1..1 for a small map.</param>
/// <param name="CanSetHome">The player may make this medbay their home base (AdminAction.SetHome with this card's index).</param>
/// <param name="Preview">A respawn ship's folder under Content\Ships for its picture, "" for none.</param>
/// <param name="Tag">A respawn ship's short "where" tag: "Space & moons", "Planets".</param>
/// <param name="Group">A respawn ship card's ship: the spawn screen shows one entry per ship and its destinations under it.</param>
/// <param name="Destination">For a ship card: Space, Moon or Planet.</param>
/// <param name="Gravity">A destination's surface gravity in g, -1 for none (space).</param>
/// <param name="Air">A destination's air density (0 none, 1 Earth-like), -1 for space.</param>
/// <param name="Body">The planet or moon's picture name under Content\Textures\GUI\Planets, "" for none.</param>
/// <param name="Info">More about a destination in plain words (oxygen, resources), "" for none.</param>
public sealed record SpawnOption(SpawnKind Kind, string Name, string Detail, bool Available,
	string Grid = "", double Distance = -1, string Place = "", SpawnStatus Status = SpawnStatus.Available, bool Home = false,
	double MapX = 0, double MapY = 0, bool HasMap = false, bool CanSetHome = false, string Preview = "", string Tag = "",
	string Group = "", SpawnDestination Destination = SpawnDestination.None, double Gravity = -1, double Air = -1, string Body = "", string Info = "");

public enum SpawnDestination : byte { None, Space, Moon, Planet }

/// <summary>Where a player without a character can spawn; the host sends it, the client answers with an index.</summary>
public sealed record SpawnMenu(SpawnOption[] Options, bool Respawn, string Message = "", string Death = "");

/// <summary>The spawn screen's order and the home base rules, apart from the game so they are unit tested.</summary>
public static class SpawnCards
{
	/// <summary>Home base first, then the player's medbays, faction and shared ones (nearest first, unknown distance last),
	/// next to the host, the suit, respawn ships in space, then planet landers by planet.</summary>
	public static List<T> Order<T>(IEnumerable<T> cards, Func<T, SpawnOption> option)
		=> cards.Select((c, i) => (c, i, o: option(c)))
			.OrderBy(x => Rank(x.o))
			.ThenBy(x => x.o.Kind is SpawnKind.Ship ? x.o.Place : "", StringComparer.OrdinalIgnoreCase)
			.ThenBy(x => x.o.Distance < 0 ? double.MaxValue : x.o.Distance)
			.ThenBy(x => x.i)
			.Select(x => x.c).ToList();

	private static int Rank(SpawnOption o) => o.Home ? 0 : o.Kind switch
	{
		SpawnKind.Home => 0,
		SpawnKind.Medbay => 1,
		SpawnKind.FactionMedbay => 2,
		SpawnKind.SharedMedbay => 3,
		SpawnKind.NearHost => 4,
		SpawnKind.Suit => 5,
		SpawnKind.Ship => o.Place.Length == 0 ? 6 : 7,
		_ => 8
	};

	/// <summary>Whether a home base can be used now, and why not, from what the server sees.</summary>
	public static (SpawnStatus Status, string Reason) HomeStatus(bool exists, bool functional, bool working, bool supplied, bool enabled, bool mayUse, bool allowed)
	{
		if (!exists) return (SpawnStatus.Destroyed, "Your home base was destroyed or removed.");
		if (!allowed) return (SpawnStatus.NotAllowed, "Your group may not respawn at a home base.");
		if (!mayUse) return (SpawnStatus.NoAccess, "You no longer have access to your home base.");
		if (!functional) return (SpawnStatus.Damaged, "Your home base is damaged.");
		if (!enabled) return (SpawnStatus.TurnedOff, "Your home base is turned off.");
		if (!supplied) return (SpawnStatus.Unpowered, "Your home base has no power.");
		if (!working) return (SpawnStatus.Unavailable, "Your home base is not working.");
		return (SpawnStatus.Available, "");
	}

	public static string StatusText(SpawnStatus status) => status switch
	{
		SpawnStatus.Available => "Available",
		SpawnStatus.Unpowered => "No power",
		SpawnStatus.Damaged => "Damaged",
		SpawnStatus.TurnedOff => "Turned off",
		SpawnStatus.NoAccess => "No access",
		SpawnStatus.Destroyed => "Destroyed",
		SpawnStatus.NotAllowed => "Not allowed",
		SpawnStatus.Cooldown => "Not yet",
		_ => "Unavailable"
	};

	/// <summary>A menu from the server made safe to show: text cleaned and bounded, numbers finite, unknown statuses unavailable.
	/// A missing card would shift the indices the answer names, so a bad card is kept, unavailable.</summary>
	public static SpawnMenu Clean(SpawnMenu menu) => menu with
	{
		Message = SocialState.Clean(menu.Message ?? "", 200),
		Death = SocialState.Clean(menu.Death ?? "", 160),
		Options = menu.Options.Select(o => o == null ? new SpawnOption(SpawnKind.Suit, "?", "", false, Status: SpawnStatus.Unavailable) : o with
		{
			Name = SocialState.Clean(o.Name ?? "", 100),
			Detail = SocialState.Clean(o.Detail ?? "", 120),
			Grid = SocialState.Clean(o.Grid ?? "", 64),
			Place = SocialState.Clean(o.Place ?? "", 64),
			Preview = RespawnShips.SafePreview(o.Preview),
			Tag = SocialState.Clean(o.Tag ?? "", 32),
			Group = SocialState.Clean(o.Group ?? "", 64),
			Destination = Enum.IsDefined(o.Destination) ? o.Destination : SpawnDestination.None,
			Gravity = double.IsFinite(o.Gravity) && o.Gravity >= 0 && o.Gravity < 100 ? o.Gravity : -1,
			Air = double.IsFinite(o.Air) && o.Air >= 0 && o.Air < 100 ? o.Air : -1,
			Body = RespawnShips.SafePreview(o.Body),
			Info = SocialState.Clean(o.Info ?? "", 300),
			Kind = Enum.IsDefined(o.Kind) ? o.Kind : SpawnKind.Medbay,
			Status = Enum.IsDefined(o.Status) ? o.Status : SpawnStatus.Unavailable,
			Available = o.Available && Enum.IsDefined(o.Status),
			Distance = double.IsFinite(o.Distance) && o.Distance >= 0 ? o.Distance : -1,
			MapX = double.IsFinite(o.MapX) ? Math.Clamp(o.MapX, -1, 1) : 0,
			MapY = double.IsFinite(o.MapY) ? Math.Clamp(o.MapY, -1, 1) : 0,
			HasMap = o.HasMap && double.IsFinite(o.MapX) && double.IsFinite(o.MapY)
		}).ToArray()
	};

	/// <summary>A distance for a spawn card: 850 m, 12.4 km, 130 km.</summary>
	public static string DistanceText(double metres) => metres < 0 || !double.IsFinite(metres) ? ""
		: metres >= 100_000 ? (metres / 1000).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " km"
		: metres >= 1000 ? (metres / 1000).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " km"
		: metres.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " m";

	/// <summary>A point's place on the spawn screen's small map: its offset from the death point (east, north in the
	/// world's X and Z), scaled so the farthest card sits on the map's edge.</summary>
	public static (double X, double Y) MapPoint(double dx, double dz, double farthest)
	{
		if (!(farthest > 0) || !double.IsFinite(dx) || !double.IsFinite(dz)) return (0, 0);
		double x = dx / farthest, y = dz / farthest, length = Math.Sqrt(x * x + y * y);
		return length > 1 ? (x / length, y / length) : (x, y);
	}
}
