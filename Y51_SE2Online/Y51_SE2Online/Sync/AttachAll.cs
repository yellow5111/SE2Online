using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Y51_SE2Online.Sync;

/// <summary>
/// Starts every attach request before awaiting any, so a client waits one host round trip for all of them instead of
/// one per request. A request that fails or answers null is left out; <c>disposed</c> true after the last answer means
/// the caller went away meanwhile and gets null (its own dispose has already sent the matching detaches).
/// </summary>
public static class AttachAll
{
	public static async Task<List<T>> Run<T>(IReadOnlyList<Func<Task<T>>> starts, Func<bool> disposed) where T : class
	{
		var pending = new Task<T>[starts.Count];
		for (int i = 0; i < pending.Length; i++)
		{
			try { pending[i] = starts[i](); }
			catch (Exception ex) { pending[i] = Task.FromException<T>(ex); }
		}
		var got = new List<T>(pending.Length);
		foreach (var task in pending)
		{
			try
			{
				if (await task is { } item) got.Add(item);
			}
			catch (Exception) { }
		}
		return disposed() ? null : got;
	}
}
