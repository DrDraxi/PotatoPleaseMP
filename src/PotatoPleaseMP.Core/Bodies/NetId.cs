namespace PotatoPleaseMP.Core.Bodies;

/// <summary>
/// Compact body id of the body stream (spec 07 §3, roadmap: the 16-bit index form of spec 01's NetId).
/// <list type="bullet">
/// <item><c>0x0000</c> none, <c>0xFFFF</c> invalid.</item>
/// <item><c>0x0001-0x7FFF</c> host-assigned per activation (<see cref="HostIdAllocator"/>).</item>
/// <item><c>0x8000-0xBFFF</c> client temporary ids <c>0x8000 | slot&lt;&lt;10 | n</c> (slot 1-15, <see cref="TempIdAllocator"/>).</item>
/// <item><c>0xC000-0xFFFE</c> static scene bodies (sorted hierarchy paths).</item>
/// </list>
/// </summary>
public readonly record struct NetId(ushort Value) : IComparable<NetId>
{
    public static readonly NetId None = new(0);
    public static readonly NetId Invalid = new(0xFFFF);

    public const ushort HostFirst = 0x0001, HostLast = 0x7FFF;
    public const ushort TempFirst = 0x8000, TempLast = 0xBFFF;
    public const ushort StaticFirst = 0xC000, StaticLast = 0xFFFE;
    public const int TempPerSlot = 1024;

    public bool IsNone => Value == 0;
    public bool IsValid => Value != 0 && Value != 0xFFFF;
    public bool IsHost => Value >= HostFirst && Value <= HostLast;
    public bool IsTemp => Value >= TempFirst && Value <= TempLast;
    public bool IsStatic => Value >= StaticFirst && Value <= StaticLast;

    /// <summary>Player slot of a temp id (1-15), 0 otherwise.</summary>
    public byte TempSlot => IsTemp ? (byte)((Value >> 10) & 0xF) : (byte)0;

    /// <summary>Temp id for <paramref name="slot"/> (1-15) and counter <paramref name="n"/> (0-1023).</summary>
    public static NetId Temp(byte slot, int n)
    {
        if (slot < 1 || slot > 15) throw new ArgumentOutOfRangeException(nameof(slot), slot, "Temp ids need slot 1-15.");
        if ((uint)n >= TempPerSlot) throw new ArgumentOutOfRangeException(nameof(n));
        return new NetId((ushort)(TempFirst | (slot << 10) | n));
    }

    /// <summary>Static id number <paramref name="index"/> (0-based) in the sorted scene table.</summary>
    public static NetId Static(int index)
    {
        if ((uint)index > StaticLast - StaticFirst) throw new ArgumentOutOfRangeException(nameof(index));
        return new NetId((ushort)(StaticFirst + index));
    }

    public int CompareTo(NetId other) => Value.CompareTo(other.Value);
    public override string ToString() => $"#{Value:X4}";
}

/// <summary>
/// Host id allocator (spec 07 §3): round-robin over 0x0001-0x7FFF; a freed id is not handed out again for
/// <see cref="QuarantineSeconds"/> (3 s), so late unreliable updates never land on a new body. No generation byte.
/// </summary>
public sealed class HostIdAllocator
{
    const int Size = NetId.HostLast + 1;
    readonly bool[] _live = new bool[Size];
    readonly double[] _freedAt = new double[Size];
    int _cursor = NetId.HostFirst - 1;

    public HostIdAllocator(double quarantineSeconds = 3.0)
    {
        QuarantineSeconds = quarantineSeconds;
        Array.Fill(_freedAt, double.NegativeInfinity);
    }

    public double QuarantineSeconds { get; }
    public int LiveCount { get; private set; }
    /// <summary>Allocations that failed (every id live or quarantined).</summary>
    public long Exhausted { get; private set; }

    public bool IsLive(NetId id) => id.IsHost && _live[id.Value];

    /// <summary>Next free id after the cursor; <see cref="NetId.None"/> when every id is live or quarantined.</summary>
    public NetId Allocate(double now)
    {
        for (int i = 0; i < NetId.HostLast; i++)
        {
            _cursor = _cursor >= NetId.HostLast ? NetId.HostFirst : _cursor + 1;
            if (_live[_cursor]) continue;
            if (now - _freedAt[_cursor] < QuarantineSeconds) continue;
            _live[_cursor] = true;
            LiveCount++;
            return new NetId((ushort)_cursor);
        }
        Exhausted++;
        return NetId.None;
    }

    /// <summary>Claims a specific id (join replay, tests). False when it is live or quarantined.</summary>
    public bool TryReserve(NetId id, double now)
    {
        if (!id.IsHost || _live[id.Value] || now - _freedAt[id.Value] < QuarantineSeconds) return false;
        _live[id.Value] = true;
        LiveCount++;
        return true;
    }

    /// <summary>Frees a live id; it enters quarantine. Freeing a non-live id does nothing and returns false.</summary>
    public bool Free(NetId id, double now)
    {
        if (!id.IsHost || !_live[id.Value]) return false;
        _live[id.Value] = false;
        _freedAt[id.Value] = now;
        LiveCount--;
        return true;
    }
}

/// <summary>
/// Client temp ids for predicted spawns (spec 07 §3): <c>0x8000 | slot&lt;&lt;10 | n</c>, 1024 in flight per player,
/// round-robin so a just-released counter is the last to come back.
/// </summary>
public sealed class TempIdAllocator
{
    readonly bool[] _inFlight = new bool[NetId.TempPerSlot];
    int _cursor = -1;

    public TempIdAllocator(byte slot)
    {
        if (slot < 1 || slot > 15) throw new ArgumentOutOfRangeException(nameof(slot), slot, "Temp ids need slot 1-15.");
        Slot = slot;
    }

    public byte Slot { get; }
    public int InFlight { get; private set; }

    /// <summary>Next temp id; <see cref="NetId.None"/> when 1024 are in flight.</summary>
    public NetId Next()
    {
        for (int i = 0; i < NetId.TempPerSlot; i++)
        {
            _cursor = (_cursor + 1) % NetId.TempPerSlot;
            if (_inFlight[_cursor]) continue;
            _inFlight[_cursor] = true;
            InFlight++;
            return NetId.Temp(Slot, _cursor);
        }
        return NetId.None;
    }

    public bool Owns(NetId id) => id.IsTemp && id.TempSlot == Slot;

    /// <summary>Returns a temp id (after <c>SpawnAdopt</c> or despawn of the prediction).</summary>
    public bool Release(NetId id)
    {
        if (!Owns(id)) return false;
        int n = id.Value & (NetId.TempPerSlot - 1);
        if (!_inFlight[n]) return false;
        _inFlight[n] = false;
        InFlight--;
        return true;
    }
}
