using System.Collections.Generic;
using Keen.VRage.DCS.Builders;
using Keen.VRage.DCS.Components;

namespace Y51_SE2Online.SyncEngine;

internal sealed class ProxyMap
{
	private readonly object _sync = new object();

	private readonly Dictionary<uint, Entity> _byId = new Dictionary<uint, Entity>();

	private readonly Dictionary<Entity, uint> _ids = new Dictionary<Entity, uint>(ReferenceEqualityComparer.Instance);

	private readonly HashSet<uint> _live = new HashSet<uint>();

	public int Count
	{
		get
		{
			lock (_sync)
			{
				return _byId.Count;
			}
		}
	}

	public int LiveCount
	{
		get
		{
			lock (_sync)
			{
				return _live.Count;
			}
		}
	}

	public Entity GetOrCreate(uint id)
	{
		lock (_sync)
		{
			if (_byId.TryGetValue(id, out Entity value))
			{
				return value;
			}
			value = EntityAllocator.AllocateStub();
			_byId[id] = value;
			_ids[value] = id;
			return value;
		}
	}

	public bool TryGet(uint id, out Entity e)
	{
		lock (_sync)
		{
			return _byId.TryGetValue(id, out e);
		}
	}

	public bool TryGetId(Entity e, out uint id)
	{
		lock (_sync)
		{
			return _ids.TryGetValue(e, out id);
		}
	}

	public void MarkLive(uint id)
	{
		lock (_sync)
		{
			_live.Add(id);
		}
	}

	public bool IsLive(uint id)
	{
		lock (_sync)
		{
			return _live.Contains(id);
		}
	}

	public List<uint> LiveIn(uint lo, uint hi)
	{
		lock (_sync)
		{
			var ids = new List<uint>();
			foreach (uint id in _live)
			{
				if (id >= lo && id <= hi) ids.Add(id);
			}
			return ids;
		}
	}

	public void MarkDead(uint id)
	{
		lock (_sync)
		{
			_live.Remove(id);
		}
	}

	public void Forget(uint id)
	{
		lock (_sync)
		{
			_live.Remove(id);
			if (_byId.Remove(id, out Entity value))
			{
				_ids.Remove(value);
			}
		}
	}
}
