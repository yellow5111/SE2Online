namespace Y51_SE2Online.Sync;

/// <summary>Join loading lines: a random starting point per join, then a new line every few seconds within the phase.</summary>
public static class LoadingFlavour
{
	public const int RotateMs = 4000;

	private static readonly string[] Download =
	{
		"Charting the Almagest system", "Receiving long-range transmissions", "Decrypting sector survey data",
		"Syncing the star charts", "Pulling flight logs from the relay", "Downloading the colony registry",
		"Copying blueprints over the comms array", "Tuning the antenna to Concordia Research Station",
		"Plotting routes across the Almagest", "Gathering the news from Kemik Station"
	};

	private static readonly string[] Engine =
	{
		"Spinning up planetary cores", "Seeding asteroids throughout the Almagest", "Calibrating Kemik's gravity well",
		"Raising the hills of Verdure", "Filling the oceans of Byblos", "Charting the island chains of Byblos",
		"Scattering ore deposits across Kemik", "Growing the forests of Verdure", "Stirring up the weather on Byblos",
		"Lighting the Almagest sun", "Settling dust on the asteroid fields", "Laying down the bedrock"
	};

	private static readonly string[] Build =
	{
		"Welding hull plates into place", "Teaching drones to dock", "Charging batteries", "Filling hydrogen tanks",
		"Testing every thruster nozzle", "Bolting rotors and pistons together", "Warming up the jump drives",
		"Sorting the cargo containers", "Restocking Concordia's medbays", "Aligning the gyroscopes",
		"Counting every last steel plate", "Polishing the cockpit glass"
	};

	private static readonly string[] Spawn =
	{
		"Printing your engineer suit", "Pressurizing your helmet", "Checking your oxygen supply",
		"Booting the suit HUD", "Loading your toolbar"
	};

	public static int PoolSize => Download.Length + Engine.Length + Build.Length + Spawn.Length;

	public static string Line(JoinStage stage, int seed, long now)
	{
		string[] pool = stage switch
		{
			JoinStage.Receiving => Download,
			JoinStage.Loading => Engine,
			JoinStage.Building => Build,
			_ => Spawn
		};
		long step = (seed & 0x7FFFFFFF) + System.Math.Max(0, now) / RotateMs;
		return pool[(int)(step % pool.Length)];
	}
}
