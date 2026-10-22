using System;
using System.Collections.Generic;

namespace Y51_SE2Online.Sync;

/// <summary>
/// One remote entity's motion samples not yet handed to the game's playback, in server-time order (a repeat of a time
/// replaces it). The playback keeps only two samples, so each is handed over once the cursor is about to need it:
/// <see cref="Due"/> picks the newest sample at or before the next frame's cursor plus one frame (that one and older go;
/// older ones would only be passed over). A teleport at the front goes at once; when nothing is due and the playback's
/// newest sample is already behind the cursor (the stream fell behind), the oldest goes so the playback is not left
/// extrapolating.
/// </summary>
public sealed class SampleBuffer<T>
{
	private readonly List<(long Time, bool Teleport, T Value)> _items = new();

	public int Count => _items.Count;

	public void Add(long time, bool teleport, T value)
	{
		int at = _items.FindLastIndex(s => s.Time <= time);
		if (at >= 0 && _items[at].Time == time) _items[at] = (time, teleport, value);
		else _items.Insert(at + 1, (time, teleport, value));
	}

	/// <summary>The sample to hand over now (the ones before it are dropped), or false to wait.</summary>
	public bool Due(long nextCursor, long frame, long newestPlayed, out T value, out long time)
	{
		int due = -1;
		for (int i = 0; i < _items.Count && (_items[i].Time <= nextCursor + frame || i == 0 && _items[i].Teleport); i++) due = i;
		if (due < 0 && _items.Count > 0 && (newestPlayed == 0 || newestPlayed < nextCursor)) due = 0;
		if (due < 0)
		{
			value = default!;
			time = 0;
			return false;
		}
		(time, _, value) = _items[due];
		_items.RemoveRange(0, due + 1);
		return true;
	}

	/// <summary>The newest held sample (the entity is gone or has no playback: it gets that one as the game would).</summary>
	public bool Last(out T value)
	{
		value = _items.Count > 0 ? _items[^1].Value : default!;
		bool any = _items.Count > 0;
		_items.Clear();
		return any;
	}
}
