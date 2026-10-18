using System;
using System.Collections.Generic;
using System.Linq;

namespace Y51_SE2Online.Core;

/// <summary>Which save a dedicated server loads when it starts from neither StartContent nor StartWorkshop.</summary>
public static class WorldPick
{
    /// <summary>A save container: its folder name (the native ContainerId) and the name the game shows.</summary>
    public readonly record struct Save(string Id, string Name);

    /// <summary><see cref="Id"/> null and <see cref="Error"/> null: create a new world named WorldName.</summary>
    public sealed record Result(string Id, string Error, string Note);

    /// <summary>
    /// SaveId must name a save (folder or shown name). Without SaveId, a save whose folder or shown name equals
    /// WorldName is loaded: players copy their "Save As" world and expect WorldName to pick it.
    /// </summary>
    public static Result Resolve(string saveId, string worldName, IReadOnlyList<Save> saves, string folder,
        IReadOnlyList<Save> gameSaves = null, string gameFolder = null)
    {
        bool explicitId = !string.IsNullOrWhiteSpace(saveId);
        string wanted = (explicitId ? saveId : worldName ?? "").Trim();
        string setting = explicitId ? "SaveId" : "WorldName";
        var found = Find(wanted, saves);
        if (found.HasValue) return new Result(found.Value.Id, null, null);

        string text = $"{setting} '{wanted}' is not a save in {folder}.";
        var elsewhere = gameSaves == null ? null : Find(wanted, gameSaves);
        if (elsewhere.HasValue)
            text += $" It is in the game's own saves: copy the whole folder {System.IO.Path.Combine(gameFolder ?? "", elsewhere.Value.Id)} into {folder}.";
        var close = Close(wanted, saves);
        if (close.Count > 0) text += " Close matches here: " + string.Join(", ", close.Select(Describe)) + ".";
        if (explicitId)
        {
            if (close.Count == 0 && !elsewhere.HasValue)
                text += saves.Count == 0 ? " That folder has no saves; copy your world's whole save folder into it."
                    : " Saves there: " + string.Join(", ", saves.Take(10).Select(Describe)) + (saves.Count > 10 ? $" and {saves.Count - 10} more." : ".");
            return new Result(null, text, null);
        }
        return new Result(null, null, close.Count > 0 || elsewhere.HasValue ? text + " Creating a new world named '" + wanted + "' instead." : null);
    }

    private static Save? Find(string wanted, IReadOnlyList<Save> saves)
    {
        foreach (var compare in new[] { StringComparison.Ordinal, StringComparison.OrdinalIgnoreCase })
        {
            foreach (var save in saves) if (string.Equals(save.Id, wanted, compare)) return save;
            foreach (var save in saves) if (string.Equals(save.Name?.Trim(), wanted, compare)) return save;
        }
        return null;
    }

    private static string Describe(Save s) => string.IsNullOrEmpty(s.Name) || s.Name == s.Id ? $"'{s.Id}'" : $"'{s.Name}' (folder '{s.Id}')";

    /// <summary>Saves whose folder or shown name is a near miss (case, spacing, a suffix such as " 1", a typo); closest first.</summary>
    public static List<Save> Close(string wanted, IReadOnlyList<Save> saves, int max = 5)
    {
        string key = Key(wanted);
        if (key.Length == 0) return new List<Save>();
        return saves.Select(s => (Save: s, Distance: Math.Min(Distance(key, Key(s.Id)), Distance(key, Key(s.Name)))))
            .Where(x => x.Distance <= Math.Max(2, key.Length / 4)).OrderBy(x => x.Distance).Take(max).Select(x => x.Save).ToList();
    }

    private static string Key(string s) => new string((s ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static int Distance(string a, string b)
    {
        if (b.Length == 0) return int.MaxValue;
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (int i = 1; i <= a.Length; i++)
        {
            int diagonal = row[0];
            row[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int above = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diagonal + (a[i - 1] == b[j - 1] ? 0 : 1));
                diagonal = above;
            }
        }
        return row[b.Length];
    }
}
