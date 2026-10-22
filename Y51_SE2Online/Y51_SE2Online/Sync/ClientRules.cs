using System;
using System.Collections.Generic;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.Sync;

/// <summary>The joining side of custom rules: apply the host's definition values, then answer so the host sends the world.</summary>
public static class ClientRules
{
	/// <summary>Set by the game side: applies the rules (hash, rules) and returns "" or why they do not fit this game.</summary>
	public static Func<string, List<AppliedRule>, string> Apply;

	/// <summary>The answer to send the host.</summary>
	public static byte[] Handle(byte[] data, IMpLog log)
	{
		string hash = "", error;
		try
		{
			var (h, rules) = RulesWire.Decode(data);
			hash = h;
			log.Info($"sync(client): the host runs custom rules {hash}: {rules.Count} definition value(s) to apply before the world arrives");
			error = Apply == null ? "this game cannot apply custom rules" : Apply(hash, rules);
		}
		catch (Exception ex) when (ex is ArgumentException or System.IO.InvalidDataException or System.IO.EndOfStreamException or FormatException)
		{
			error = "the server's custom rules could not be read (" + ex.Message + ")";
		}
		if (!string.IsNullOrEmpty(error)) log.Warn("sync(client): custom rules not applied: " + error);
		return RulesWire.EncodeAck((byte)SyncMsg.RulesAck, hash, error);
	}
}
