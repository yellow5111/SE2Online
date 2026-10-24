using System;
using System.IO;
using System.Text.Json;

namespace Y51_SE2Online.Sync;

/// <summary>
/// audit.jsonl: one JSON line per command, group change and cleanup removal (time, channel, actor, what, result), next to
/// server.json on a dedicated server and in the plugin's data folder on a co-op host. Rotated to audit.1.jsonl at 10 MB.
/// Never throws: a full disk must not stop a command.
/// </summary>
public static class AuditLog
{
	public const long MaxBytes = 10 * 1024 * 1024;
	private static readonly object Sync = new();
	public static string Path { get; set; }

	public static void Write(string channel, string actor, string action, string result, object detail = null)
	{
		string path = Path;
		if (string.IsNullOrEmpty(path)) return;
		try
		{
			string line = JsonSerializer.Serialize(new { Utc = DateTime.UtcNow.ToString("o"), Channel = channel, Actor = actor, Action = action, Result = result, Detail = detail });
			lock (Sync)
			{
				var info = new FileInfo(path);
				if (info.Exists && info.Length > MaxBytes)
				{
					string old = System.IO.Path.ChangeExtension(path, ".1.jsonl");
					File.Move(path, old, true);
				}
				File.AppendAllText(path, line + "\n");
			}
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
	}
}
