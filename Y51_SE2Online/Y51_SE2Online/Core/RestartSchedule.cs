using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Y51_SE2Online.Core;

/// <summary>When a dedicated server restarts itself, and what players are told beforehand.</summary>
public static class RestartSchedule
{
    public static bool TryParseTime(string text, out TimeSpan time)
        => TimeSpan.TryParseExact(text?.Trim(), new[] { @"h\:mm", @"hh\:mm" }, CultureInfo.InvariantCulture, out time) && time >= TimeSpan.Zero && time < TimeSpan.FromDays(1);

    /// <summary>
    /// The first restart after <paramref name="now"/> and after the startup grace, from daily local times and/or an
    /// interval counted from startup. Null when neither is configured. Times that do not exist on a clock-change day
    /// (spring forward) move to the next hour.
    /// </summary>
    public static DateTime? Next(DateTime now, DateTime started, IReadOnlyCollection<string> times, double everyHours, int minUptimeMinutes, TimeZoneInfo zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        DateTime earliest = new[] { now, started.AddMinutes(minUptimeMinutes) }.Max();
        var candidates = new List<DateTime>();
        foreach (string text in times ?? Array.Empty<string>())
        {
            if (!TryParseTime(text, out TimeSpan time)) continue;
            for (int day = 0; day <= 2; day++)
            {
                DateTime at = earliest.Date.AddDays(day) + time;
                if (zone.IsInvalidTime(at)) at = at.AddHours(1);
                if (at > earliest) { candidates.Add(at); break; }
            }
        }
        if (everyHours > 0)
        {
            var step = TimeSpan.FromHours(everyHours);
            long steps = Math.Max(1, (long)Math.Floor((earliest - started).Ticks / (double)step.Ticks) + 1);
            candidates.Add(started + TimeSpan.FromTicks(step.Ticks * steps));
        }
        return candidates.Count == 0 ? null : candidates.Min();
    }

    /// <summary>The configured warning (in minutes) that is due now, below the last one announced; 0 if none.</summary>
    public static int WarnDue(IReadOnlyCollection<int> warnMinutes, int lastAnnouncedMinutes, double secondsLeft)
        => (warnMinutes ?? Array.Empty<int>()).Where(m => m < lastAnnouncedMinutes && secondsLeft <= m * 60).DefaultIfEmpty(0).Min();

    public static string WarnText(int minutes)
        => $"Scheduled server restart in {minutes} min - finish up and get somewhere safe.";
}

/// <summary>The message of the day joiners see after they spawn.</summary>
public static class Motd
{
    public const int MaxText = 1000;
    public const int MaxTitle = 100;

    public static string Expand(string template, string player, string server, int online, int max)
    {
        if (string.IsNullOrWhiteSpace(template)) return "";
        string text = template.Replace("{player}", player ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{server}", server ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{online}", online.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{max}", max.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("\r\n", "\n").Trim();
        return text.Length > MaxText ? text[..MaxText] : text;
    }
}
