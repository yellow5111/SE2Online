using System.Linq;

namespace Y51_SE2Online.Core;

/// <summary>Chat warnings while a dedicated server with players online counts down to a requested stop.</summary>
public static class ShutdownNotice
{
    private static readonly int[] Marks = { 600, 300, 120, 60, 30, 15, 10, 5, 4, 3, 2, 1 };

    /// <summary>True once the countdown has reached a mark below the last announcement (late frames announce the time actually left).</summary>
    public static bool Due(int lastAnnounced, int secondsLeft) => Marks.Any(m => m < lastAnnounced && secondsLeft <= m);

    public static string Text(int secondsLeft, bool restart = false)
    {
        string left = secondsLeft >= 120 && secondsLeft % 60 == 0 ? secondsLeft / 60 + " min" : secondsLeft + " s";
        string what = restart ? "Server restarting" : "Server shutting down";
        return secondsLeft >= 10
            ? $"{what} in {left} - get to a safe spot and stop what you're doing."
            : $"{what} in {left}.";
    }

    public const string Now = "Server is shutting down now. Saving the world.";
    public const string RestartNow = "Server is restarting now. Saving the world; join again in a minute or two.";
}
