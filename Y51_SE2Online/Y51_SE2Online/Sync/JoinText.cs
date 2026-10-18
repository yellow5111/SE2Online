namespace Y51_SE2Online.Sync;

public readonly record struct JoinText(string? Toast, string? Loading, int ToastSeconds)
{
	public const string Title = "Multiplayer (Space Engineers 2 Online)";

	public static readonly JoinText None = new JoinText(null, null, 0);
}
