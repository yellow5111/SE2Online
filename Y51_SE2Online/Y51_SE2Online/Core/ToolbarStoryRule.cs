using System;
using System.Collections.Concurrent;
using System.Reflection;

namespace Y51_SE2Online.Core;

/// <summary>
/// A player's toolbar request that arrived naming no block (the block had no network id in the player's game, so SE2 sent
/// it as null) is turned into a request for no toolbar: its toolbar index becomes -1, which
/// BlockToolbarServerComponent.TryGetAt answers with null before it touches the block. Without it the server's TryGetAt
/// faulted on the null block. Kept free of game types so the tests can run it on their own state machines.
/// </summary>
public static class ToolbarStoryRule
{
	private static readonly ConcurrentDictionary<Type, (FieldInfo Index, FieldInfo[] Blocks)?> Fields = new();

	/// <summary>
	/// For a boxed state machine (a restored story): true when one of its block fields names no block, after setting its
	/// toolbar index to -1. <paramref name="namesNoBlock"/> reads a block field's value.
	/// </summary>
	public static bool Disarm(object machine, Func<object, bool> namesNoBlock)
	{
		if (Fields.GetOrAdd(machine.GetType(), Find) is not { } fields) return false;
		foreach (var block in fields.Blocks)
		{
			if (!namesNoBlock(block.GetValue(machine))) continue;
			fields.Index.SetValue(machine, -1);
			return true;
		}
		return false;
	}

	private static (FieldInfo, FieldInfo[])? Find(Type machine)
	{
		var index = machine.GetField("toolbarIndex", BindingFlags.Instance | BindingFlags.Public);
		if (index?.FieldType != typeof(int)) return null;
		var blocks = Array.FindAll(machine.GetFields(BindingFlags.Instance | BindingFlags.Public), f => f.Name is "toolbarBlock" or "toolbarEntity");
		return blocks.Length == 0 ? null : (index, blocks);
	}
}
