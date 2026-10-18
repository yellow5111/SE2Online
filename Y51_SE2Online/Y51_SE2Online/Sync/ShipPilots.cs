using System.Collections.Generic;

namespace Y51_SE2Online.Sync;

/// <summary>
/// One pilot per ship: of the players in its control seats (cockpits, control seats), the first to sit flies it, a tie
/// going to the lower ClientId (the host's player is 1). The others ride as passengers until the pilot stands up, dies
/// or leaves; then the next one in a control seat flies it without having to sit down again.
/// </summary>
public static class ShipPilots
{
    public const ushort Nobody = 0;

    public static ushort Pilot(IEnumerable<(ushort Client, long SeatedAtMs)> controlSeats)
    {
        ushort pilot = Nobody;
        long first = long.MaxValue;
        foreach (var (client, seatedAt) in controlSeats)
        {
            if (seatedAt < first || (seatedAt == first && client < pilot))
            {
                pilot = client;
                first = seatedAt;
            }
        }
        return pilot;
    }

    public static string PassengerNotice(string? pilotName) =>
        $"Ship is being piloted by {(string.IsNullOrWhiteSpace(pilotName) ? "another player" : pilotName)}; you're a passenger";
}
