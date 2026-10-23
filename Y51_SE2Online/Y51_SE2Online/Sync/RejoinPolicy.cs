namespace Y51_SE2Online.Sync;

/// <summary>
/// When a player whose connection was lost joins again by itself (F20): each automatic rejoin waits longer than the last,
/// and after <see cref="Attempts"/> in a row the player decides (Retry). A join the player starts, or a session that stays
/// in the world for <see cref="StableMs"/>, counts from zero again. One per game: every rejoin is a new join session.
/// </summary>
public sealed class RejoinPolicy
{
	public static readonly long[] DelaysMs = { 3000, 20000, 60000 };

	public const long StableMs = 10 * 60 * 1000;

	/// <summary>How long a rejoin that is due waits for the game to finish loading or unloading a world before it goes anyway.</summary>
	public const long TransitionWaitMs = 60000;

	public static int Attempts => DelaysMs.Length;

	/// <summary>How long SE2 Online holds Keen's handshake open for the host's join check (Keen's own wait is 30 s).</summary>
	public const long HandshakeHoldMs = 120000;

	/// <summary>Keen's own wait for the server's hello once SE2 Online stops holding it.</summary>
	public const long KeenHelloMs = 30000;

	/// <summary>
	/// Whether Keen's handshake is held open in this stage: only while a join is under way. A join that is lost (its
	/// rejoin timed out), failed or refused lets Keen give up within <see cref="KeenHelloMs"/>, so its world transition
	/// ends long before a rejoin due then gives up on it (<see cref="TransitionWaitMs"/>).
	/// </summary>
	public static bool HoldsHandshake(JoinStage stage) => stage is not (JoinStage.Rejected or JoinStage.Failed or JoinStage.Idle or JoinStage.Lost);

	public int Rejoins { get; private set; }

	// An automatic rejoin was asked for and its join has not started yet.
	private bool _pending;

	/// <summary>A join starts (its mode is installed): an automatic rejoin counts one more, one the player started begins
	/// counting again.</summary>
	public void JoinStarting()
	{
		if (_pending) Rejoins++;
		else Rejoins = 0;
		_pending = false;
	}

	/// <summary>The connection was lost: how long to wait before rejoining by itself, or null when the player decides.</summary>
	public long? DelayAfterLoss() => Rejoins < DelaysMs.Length ? DelaysMs[Rejoins] : null;

	/// <summary>The automatic rejoin goes now: the next join to start counts as one.</summary>
	public void Rejoining() => _pending = true;

	/// <summary>The automatic rejoin never started (the session change failed): the player's own next join counts from zero.</summary>
	public void RejoinFailed() => _pending = false;

	/// <summary>True (and counting from zero again) once a session that came from rejoins has stayed in the world long enough.</summary>
	public bool Stable(long inWorldMs)
	{
		if (Rejoins == 0 || inWorldMs <= StableMs) return false;
		Rejoins = 0;
		return true;
	}

	/// <summary>
	/// What a rejoin due at <paramref name="dueMs"/> does now: it waits while the game is loading or unloading a world, and
	/// gives up (the player decides) when that transition outlasts <see cref="TransitionWaitMs"/>. It cannot go then: the
	/// game refuses to leave a world in mid-transition, and the session change gives up on it after 20 s with networking
	/// already stopped.
	/// </summary>
	public static RejoinStep Next(long nowMs, long dueMs, bool transitioning)
		=> nowMs < dueMs ? RejoinStep.Wait : !transitioning ? RejoinStep.Go : nowMs >= dueMs + TransitionWaitMs ? RejoinStep.GiveUp : RejoinStep.Wait;

	/// <summary>A lost connection leaves a failed or refused join as it is (its reason shown, no automatic rejoin): a refusal or
	/// kick always, a failed join while no world of it is up or the game is already going back to the main menu.</summary>
	public static bool KeepsFailure(JoinStage stage, bool worldUp, bool backToMenu)
		=> stage == JoinStage.Rejected || stage == JoinStage.Failed && (!worldUp || backToMenu);

	/// <summary>
	/// Where a join goes when its world loads after it already counted as failed (connect timeout) or lost (a rejoin that
	/// timed out): Lost (and rejoining) when the connection was lost meanwhile, its session being gone, else on loading.
	/// Null leaves the stage as it is.
	/// </summary>
	public static JoinStage? AfterLateLoad(JoinStage stage, bool lossReported) => stage switch
	{
		JoinStage.Failed => lossReported ? JoinStage.Lost : JoinStage.Loading,
		JoinStage.Lost when !lossReported => JoinStage.Loading,
		_ => null,
	};

	/// <summary>
	/// What an event does to a join, decided on the main thread (ClientWorld.MainTick) one event at a time. Lost means an
	/// automatic rejoin follows (ScheduleRejoin, up to <see cref="Attempts"/> in a row); Failed and Rejected wait for the
	/// player; any other result leaves the stage as it is.
	/// <list type="bullet">
	/// <item>Leaving is final, and a refusal or kick (<paramref name="refused"/>: the server gave a reason) never rejoins.</item>
	/// <item>A lost connection rejoins unless the join already failed with no world of it left.</item>
	/// <item>A failed load, a connect timeout or the game's own join ending is one more loss for an automatic rejoin under
	/// way (the next waits longer), and failed for a join the player started.</item>
	/// </list>
	/// </summary>
	public static JoinStage Decide(JoinEvent e, JoinStage stage, bool worldUp, bool backToMenu, bool refused, bool rejoinOn, int rejoins)
	{
		if (e == JoinEvent.Leave || stage is JoinStage.Left or JoinStage.GaveUp) return e == JoinEvent.Leave ? JoinStage.Left : stage;
		if (stage == JoinStage.Rejected) return JoinStage.Rejected;
		if (refused) return JoinStage.Failed;
		if (e == JoinEvent.Loss) return KeepsFailure(stage, worldUp, backToMenu) ? stage : JoinStage.Lost;
		return rejoinOn && rejoins > 0 ? JoinStage.Lost : JoinStage.Failed;
	}
}

public enum RejoinStep { Wait, Go, GiveUp }

public enum JoinEvent { Loss, LoadFault, Timeout, JoinEnded, Leave }
