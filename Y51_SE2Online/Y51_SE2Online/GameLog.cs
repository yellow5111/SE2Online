using System;
using System.IO;
using Keen.VRage.Library.Diagnostics;
using Y51_SE2Online.Core;

namespace Y51_SE2Online;

internal sealed class GameLog : IMpLog
{
	public static readonly GameLog Instance = new GameLog();

	public void Info(string message)
	{
		Write(LogSeverity.Info, message);
	}

	public void Warn(string message)
	{
		Write(LogSeverity.Warning, message);
	}

	public void Error(string message)
	{
		Write(LogSeverity.Error, message);
	}

	/// <summary>Set on the game thread while an RCON command runs: the lines it logs become the reply.</summary>
	[ThreadStatic] internal static System.Text.StringBuilder Capture;

	public static void Write(LogSeverity severity, string message)
	{
		Dedicated.DedicatedConsoleLog.Write(severity.ToString(), message);
		if (Dedicated.DedicatedRuntime.Requested) LogRing.Server.Add(severity.ToString(), message);
		Capture?.Append(message.StartsWith("console: ", StringComparison.Ordinal) ? message.Substring(9) : message).Append('\n');
		try
		{
			string text = LogFormat.Line(LogFormat.Level(severity.ToString()), message);
			Log log = Log.Default;
			if (log != null)
			{
				log.WriteLine(severity, text);
				return;
			}
			Console.Error.WriteLine($"{severity}: {text}");
		}
		catch (Exception)
		{
		}
	}

	public static string LogDirectory()
	{
		try
		{
			if (Log.Default?.Stream is FileStream fileStream)
			{
				string directoryName = Path.GetDirectoryName(fileStream.Name);
				if (!string.IsNullOrEmpty(directoryName) && Directory.Exists(directoryName))
				{
					return directoryName;
				}
			}
		}
		catch (Exception)
		{
		}
		string text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpaceEngineers2", "Temp", "Logs");
		Directory.CreateDirectory(text);
		return text;
	}
}
