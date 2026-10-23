using System;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.KeenNetcode;

/// <summary>
/// Diagnostic log of replication units a joined player's game could not read or apply (see <see cref="UnitFailures"/>).
/// Called on failure paths only; never throws.
/// </summary>
internal static class UnitFailureLog
{
	private static readonly UnitFailures Failures = new();

	public static void Note(string kind, string what, string detail)
	{
		try
		{
			// A joined player's game only: a host's or server's own failures are logged where they happen.
			if (!MpPlugin.Online || MpPlugin.App is not { ClientSession: { } session, ServerSession: null }) return;
			var (line, summary) = Failures.Note(session, kind, what, detail, MpClock.NowMs());
			if (line != null) GameLog.Instance.Warn("units: " + line);
			if (summary != null) GameLog.Instance.Warn("units: in the last minute: " + summary);
		}
		catch (Exception) { }
	}

	public static void NotePairing(string message)
	{
		try
		{
			if (!MpPlugin.Online || MpPlugin.App is not { ClientSession: not null, ServerSession: null }) return;
			var (what, detail) = UnitFailures.DescribePairing(message);
			Note("pairing", what, detail);
		}
		catch (Exception) { }
	}

	public static void NoteError(string kind, string what, Exception ex)
	{
		try { Note(kind, what, (ex.InnerException ?? ex).GetType().Name + ": " + (ex.InnerException ?? ex).Message.Split('\n')[0]); }
		catch (Exception) { }
	}
}
