using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

/// <summary>One respawn ship in the spawn screen's top level, with the menu cards (flat indices) of its destinations.</summary>
internal sealed class ShipGroup
{
    public string Name = "";
    public readonly List<(SpawnOption Option, int Index)> Destinations = new();

    private SpawnOption _card;

    /// <summary>The ship as one card: its picture, tag and description from its destinations; usable when any of them is.
    /// Made once, so the enlarged panel sees the same card and keeps its picture.</summary>
    public SpawnOption Card => _card ??= MakeCard();

    private SpawnOption MakeCard()
    {
        {
            var first = Destinations[0].Option;
            var usable = Destinations.FirstOrDefault(d => d.Option.Available).Option;
            string Pick(System.Func<SpawnOption, string> field) => Destinations.Select(d => field(d.Option)).FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? "";
            return new SpawnOption(SpawnKind.Ship, Name, Pick(o => o.Detail), usable != null,
                Status: usable?.Status ?? first.Status, Preview: Pick(o => o.Preview), Tag: Pick(o => o.Tag), Group: Name);
        }
    }
}

/// <summary>The spawn screen's two levels: medbays, home, suit and the like as they are, and one entry per respawn ship
/// (cards of Kind Ship grouped by Group) whose destinations open under it. Server order is kept: a ship sits where its
/// first destination is.</summary>
internal static class SpawnGroups
{
    /// <summary>Each top-level entry: a card (Group null) or a ship (Index -1).</summary>
    public static List<(SpawnOption Option, int Index, ShipGroup Group)> Top(SpawnOption[] options, System.Func<int, bool> include = null)
    {
        var top = new List<(SpawnOption, int, ShipGroup)>();
        var groups = new Dictionary<string, ShipGroup>();
        for (int i = 0; i < options.Length; i++)
        {
            var o = options[i];
            if (include != null && !include(i)) continue;
            if (o.Kind != SpawnKind.Ship || string.IsNullOrEmpty(o.Group)) { top.Add((o, i, null)); continue; }
            if (!groups.TryGetValue(o.Group, out var group))
            {
                group = groups[o.Group] = new ShipGroup { Name = o.Group };
                top.Add((null, -1, group));
            }
            group.Destinations.Add((o, i));
        }
        return top.Select(t => t.Item3 != null ? (t.Item3.Card, -1, t.Item3) : t).ToList();
    }

    public static bool IsDestination(SpawnOption o) => o.Kind == SpawnKind.Ship && !string.IsNullOrEmpty(o.Group);

    /// <summary>A card's name for hints and logs: a destination says its ship too ("Atmospheric Lander to Verdure").</summary>
    public static string Label(SpawnOption o) => o.Kind == SpawnKind.Ship && !string.IsNullOrEmpty(o.Group) && o.Destination != SpawnDestination.None ? o.Group + " to " + o.Name : o.Name;

    public static string Type(SpawnOption o) => o.Destination switch
    {
        SpawnDestination.Space => "Space",
        SpawnDestination.Moon => "Moon",
        SpawnDestination.Planet => "Planet",
        _ => string.IsNullOrEmpty(o.Place) ? "Space" : "Planet"
    };

    public static string Gravity(SpawnOption o) => o.Gravity < 0 ? "No gravity" : "Gravity " + o.Gravity.ToString("0.00", CultureInfo.InvariantCulture) + " g";

    public static string Air(SpawnOption o) => o.Air <= 0 ? "Atmosphere: no" : "Atmosphere: yes, density " + o.Air.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>A destination's facts in reading order: type, gravity, atmosphere, distance.</summary>
    public static IEnumerable<string> Facts(SpawnOption o)
    {
        yield return Type(o);
        yield return Gravity(o);
        yield return Air(o);
        string distance = SpawnCards.DistanceText(o.Distance);
        if (distance.Length > 0) yield return distance + " from where you died";
    }
}
