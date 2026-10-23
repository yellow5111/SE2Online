using System;
using System.Collections.Generic;

namespace Y51_SE2Online.Sync;

/// <summary>
/// SE2 Online's join loading screen: what it says and where everything sits, in a 1920x1080 design space that the screen
/// scales uniformly (the dark band at the bottom stretches to the full width on ultrawide). Pure, so the offline mockup
/// (Tests, loadscreen-art) draws from the same values the game does.
/// </summary>
public static class LoadScreenLayout
{
	public const double Width = 1920, Height = 1080;

	// Colours as ARGB.
	public const uint Amber = 0xFFF0A01E, White = 0xFFFFFFFF, Muted = 0xFFB4BECA, Dim = 0xFF8792A0, Track = 0x33FFFFFF;
	public const uint ScreenDim = 0x30000000, Band = 0xE6080C12, BandEdge = 0x99F0A01E;

	/// <summary>Where the dark band starts (it reaches the bottom edge; above it a short fade). Below the engineers' boots.</summary>
	public const double BandTop = 836;

	// Left column: logo, wordmark and the server. Right column: the step, its bar and detail. The tip runs underneath.
	public const double Left = 96, Right = 1824, Column = 760;
	public const double LogoTop = 862, LogoSize = 66;
	public const double WordmarkLeft = 176, WordmarkTop = 856, WordmarkSize = 40;
	public const double ServerTop = 908, ServerSize = 20;
	public const double StageTop = 856, StageSize = 28;
	public const double BarTop = 898, BarHeight = 8;
	public const double DetailTop = 914, DetailSize = 19;
	public const double TipTop = 990, TipSize = 19;

	public const int TipEveryMs = 7000;

	/// <summary>The website's tips (se2-online.yellow51.dev): key, title, text.</summary>
	public static readonly (string Key, string Title, string Text)[] Tips =
	{
		("F3", "Players and server info", "The player list, every setting the server runs with, and for admins the bans and whitelist."),
		("Enter", "Chat", "Talk to everyone. /trade name asks a player to trade, /mute name silences their voice, /lockdown freezes a hack on your grids."),
		("I", "Push to talk", "Hold to speak. Voices fade with distance, so walk over to whoever you want to talk to."),
		("N", "Emote wheel", "Hold to pick an emote. Some emotes start a friendly fist fight with the player in front of you."),
		("O", "Suit broadcast", "Turns your suit antenna, and with it your name tag, on or off for other players."),
		("Alt+F10", "Admin panel", "Hosts and admins kick and ban, toggle PvP, set PCU limits, place safe zones and control the sun, all live."),
		("F11", "Connection stats", "Press until the Y51_SE2Online page shows: ping, bandwidth and sync health, handy for bug reports."),
		("Keys", "Rebind anything", "Every multiplayer key above can be changed in Multiplayer > Settings."),
		("Browser", "Find the right server", "Filter for compatible builds and free slots, sort by players or country, and star favorites to find them again."),
		("Grids", "Share your builds", "Claim, share with your faction or everyone, or transfer a grid from the ownership strip in its control panel."),
		("Hosting", "Change settings live", "Apply host settings while playing: inventory and cargo size, resource use, player limit and more, no restart needed."),
		("Reports", "Send the log", "For bugs, attach the newest SpaceEngineers2 log from %AppData%\\SpaceEngineers2\\Temp\\Logs in the community chat."),
	};

	public static (string Key, string Title, string Text) Tip(int seed, long now) => Tips[(int)(((seed & 0x7FFFFFFF) + Math.Max(0, now) / TipEveryMs) % Tips.Length)];

	/// <summary>The game's own load (download, then the game building its session) fills the bar to here; the wait for
	/// the world around the player (<see cref="WorldReadiness"/>) fills the rest.</summary>
	public const double LoadShare = 55;

	public static double Percent(JoinStage stage, double loadPercent, double readiness)
		=> stage == JoinStage.Playing ? LoadShare + (100 - LoadShare) * Math.Clamp(readiness, 0, 1) : LoadShare * Math.Clamp(loadPercent, 0, 100) / 100;

	public static string StageLine(JoinStage stage) => stage switch
	{
		JoinStage.Receiving => "Receiving world...",
		JoinStage.Loading or JoinStage.Building => "Loading world...",
		JoinStage.WaitingForCharacter => "Spawning your engineer...",
		_ => "Joining..."
	};

	/// <summary>Everything the screen shows at one moment.</summary>
	public readonly record struct View(string Server, string World, string Stage, string Detail, double Percent, string TipKey, string TipText);

	/// <summary>The values the offline mockup reads (Tests write them as JSON).</summary>
	public static IEnumerable<(string Name, object Value)> Values()
	{
		foreach (var f in typeof(LoadScreenLayout).GetFields())
			if (f.IsLiteral && f.FieldType != typeof(string)) yield return (f.Name, f.GetValue(null)!);
	}
}
