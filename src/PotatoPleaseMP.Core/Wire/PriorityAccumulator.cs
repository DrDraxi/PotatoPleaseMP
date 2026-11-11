namespace PotatoPleaseMP.Core.Wire;

/// <summary>
/// Unreliable stream slots for one peer (spec 01 §5.3). Each flush adds <c>priority · dt</c> to every dirty slot,
/// writes the highest accumulated ones until the datagram is full, zeroes those and lets the rest keep accumulating,
/// so nothing starves (far bodies still get through at a few Hz). Priorities are interest for that peer (distance,
/// speed, just released); a governor can scale them per peer.
/// </summary>
public sealed class PriorityAccumulator : IDisposable
{
    /// <summary>Writes the slot's current payload.</summary>
    public delegate void PayloadWriter(NetWriter w);

    sealed class Slot
    {
        public uint Key;
        public MsgId Id;
        public float Priority;
        public double Acc;
        public bool Dirty;
        public PayloadWriter Writer = null!;
        public int LastWrittenTick = -1;
    }

    readonly Dictionary<uint, Slot> _slots = new();
    readonly List<Slot> _order = new();
    readonly NetWriter _tmp = new(256);
    int _tick;

    public int Count => _slots.Count;
    /// <summary>Dirty slots that did not fit their datagram (deferral count, a dev stat).</summary>
    public long Deferred { get; private set; }
    public long Written { get; private set; }
    /// <summary>Multiplier applied to every priority (spec 20 governor for a struggling peer).</summary>
    public float Scale { get; set; } = 1f;

    /// <summary>Adds or updates a slot and marks it dirty.</summary>
    public void Set(uint key, MsgId id, float priority, PayloadWriter writer)
    {
        if (!_slots.TryGetValue(key, out var s)) _slots[key] = s = new Slot { Key = key };
        s.Id = id;
        s.Priority = priority;
        s.Writer = writer;
        s.Dirty = true;
    }

    public void MarkDirty(uint key) { if (_slots.TryGetValue(key, out var s)) s.Dirty = true; }
    public void Remove(uint key) => _slots.Remove(key);
    public bool IsDirty(uint key) => _slots.TryGetValue(key, out var s) && s.Dirty;
    /// <summary>Flush index at which a slot was last written (-1 never).</summary>
    public int LastWrittenTick(uint key) => _slots.TryGetValue(key, out var s) ? s.LastWrittenTick : -1;

    /// <summary>Fills <paramref name="dgram"/>, keeping <paramref name="reserve"/> bytes free. Returns the slots written.</summary>
    public int Fill(DatagramBuilder dgram, double dt, int reserve = 0)
    {
        _tick++;
        _order.Clear();
        foreach (var s in _slots.Values)
        {
            if (!s.Dirty) continue;
            s.Acc += Math.Max(0f, s.Priority * Scale) * dt + 1e-9; // epsilon: zero-priority slots still drain eventually
            _order.Add(s);
        }
        _order.Sort((a, b) => b.Acc.CompareTo(a.Acc));
        int written = 0;
        foreach (var s in _order)
        {
            if (dgram.Remaining - reserve < 3) { Deferred++; continue; }
            _tmp.Reset();
            s.Writer(_tmp);
            if (DatagramBuilder.Cost(_tmp.Length) > dgram.Remaining - reserve || !dgram.TryAdd(s.Id, _tmp.Span))
            {
                Deferred++;
                continue;
            }
            s.Acc = 0;
            s.Dirty = false;
            s.LastWrittenTick = _tick;
            written++;
        }
        Written += written;
        return written;
    }

    public int FlushCount => _tick;

    public void Dispose() => _tmp.Dispose();
}
