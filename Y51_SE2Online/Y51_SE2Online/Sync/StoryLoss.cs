namespace Y51_SE2Online.Sync;

/// <summary>
/// F71: what happens to a story one side could not read or answer. The game's own code awaits every request it sends and
/// never expects one to be skipped; SE2 Online's netcode skips some (a value naming something the other side lacks).
/// </summary>
public static class StoryLoss
{
    // A skipped answer is tens of bytes; a big station's trade screen alone asks dozens of prices each time it opens.
    public const int ClientAnswersPerFrame = 32, ServerLostPerMinute = 2048;

    /// <summary>
    /// Whether a story this side could not read is answered as skipped (else the sender waits for it forever). The server
    /// answers every one until the player passes the minute's limit, which disconnects it: the trade screen asks one price
    /// per item stack in one frame, and stories past 32 used to hang it. A player's game answers the first 32 of a frame.
    /// </summary>
    public static bool Answer(bool server, int lostThisFrame, int lostThisMinute)
        => server ? lostThisMinute <= ServerLostPerMinute : lostThisFrame <= ClientAnswersPerFrame;

    /// <summary>
    /// The trade screen lists an item as one the station buys when its price came back above zero; a price request that
    /// failed (null) leaves only that item untradable. The game's screen waited for every price together and one failure
    /// left the station's whole list empty.
    /// </summary>
    public static bool Tradable(float? price) => price is > 0f;
}
