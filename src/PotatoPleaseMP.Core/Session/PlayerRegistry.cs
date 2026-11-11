using System.Text;
using PotatoPleaseMP.Core.Net;

namespace PotatoPleaseMP.Core.Session;

/// <summary>
/// The player roster (spec 02 §3.6): slots (0 = host, 1.. clients; slot = colour and default station), lookups by
/// <see cref="PeerId"/>, slot and SteamId, slot reservations for rejoins, name sanitising. The host owns the truth;
/// clients keep a mirror filled from Welcome / PlayerAdded / PlayerRemoved / PlayerUpdate.
/// </summary>
public sealed class PlayerRegistry
{
    /// <summary>Wire/array capacity. The session's <see cref="MaxPlayers"/> is the real limit (2..4 in normal play).</summary>
    public const int MaxSlots = 8;
    /// <summary><c>SaveSlots.MaxNameLength</c>.</summary>
    public const int MaxNameChars = 24;

    readonly PlayerInfo?[] _slots = new PlayerInfo?[MaxSlots];
    readonly Dictionary<ulong, (byte Slot, double Since, double Until)> _reserved = new();
    readonly List<PlayerInfo> _ordered = new();
    int _maxPlayers = 4;

    public int MaxPlayers
    {
        get => _maxPlayers;
        set => _maxPlayers = Math.Clamp(value, 1, MaxSlots);
    }

    /// <summary>Players ordered by slot (includes the local one).</summary>
    public IReadOnlyList<PlayerInfo> Players => _ordered;
    public int Count => _ordered.Count;
    public IReadOnlyDictionary<ulong, (byte Slot, double Since, double Until)> Reservations => _reserved;

    public PlayerInfo? BySlot(byte slot) => slot < MaxSlots ? _slots[slot] : null;
    public PlayerInfo? ByPeer(PeerId peer) => _ordered.FirstOrDefault(p => p.Peer == peer);
    public PlayerInfo? BySteamId(ulong steamId) => steamId == 0 ? null : _ordered.FirstOrDefault(p => p.SteamId == steamId);
    public PlayerInfo? Local => _ordered.FirstOrDefault(p => p.IsLocal);

    /// <summary>All slots below <see cref="MaxPlayers"/> hold a live player (reservations do not count).</summary>
    public bool IsFull => _ordered.Count >= _maxPlayers;

    /// <summary>
    /// Picks a client slot (never 0): the joiner's own reservation if it has one, else the lowest free slot nobody
    /// reserved, else the oldest reservation is reclaimed. Null when every slot is live.
    /// </summary>
    public byte? AllocateSlot(ulong steamId, double now)
    {
        ExpireReservations(now);
        if (steamId != 0 && _reserved.TryGetValue(steamId, out var own) && own.Slot < _maxPlayers && _slots[own.Slot] == null)
            return own.Slot;
        var reservedSlots = _reserved.Values.Select(r => r.Slot).ToHashSet();
        for (byte s = 1; s < _maxPlayers; s++)
            if (_slots[s] == null && !reservedSlots.Contains(s)) return s;
        foreach (var kv in _reserved.OrderBy(r => r.Value.Since))
            if (kv.Value.Slot < _maxPlayers && _slots[kv.Value.Slot] == null)
            {
                _reserved.Remove(kv.Key);
                return kv.Value.Slot;
            }
        return null;
    }

    /// <summary>Adds (or replaces) the row for a slot. Consumes that player's reservation.</summary>
    public PlayerInfo Add(PlayerInfo p)
    {
        if (p.Slot >= MaxSlots) throw new ArgumentOutOfRangeException(nameof(p), $"Slot {p.Slot} >= {MaxSlots}.");
        if (_slots[p.Slot] != null) _ordered.Remove(_slots[p.Slot]!);
        _slots[p.Slot] = p;
        _ordered.Add(p);
        _ordered.Sort((a, b) => a.Slot.CompareTo(b.Slot));
        if (p.SteamId != 0) _reserved.Remove(p.SteamId);
        return p;
    }

    public PlayerInfo? Remove(byte slot)
    {
        var p = BySlot(slot);
        if (p == null) return null;
        _slots[slot] = null;
        _ordered.Remove(p);
        return p;
    }

    public void Reserve(ulong steamId, byte slot, double now, double seconds)
    {
        if (steamId == 0 || slot == 0) return;
        _reserved[steamId] = (slot, now, now + seconds);
    }

    public void ExpireReservations(double now)
    {
        foreach (var k in _reserved.Where(r => r.Value.Until <= now).Select(r => r.Key).ToList()) _reserved.Remove(k);
    }

    public void Clear()
    {
        Array.Clear(_slots);
        _ordered.Clear();
        _reserved.Clear();
    }

    /// <summary>Strips control characters, trims, clips to 24 chars; empty becomes "Player".</summary>
    public static string SanitizeName(string? raw)
    {
        var sb = new StringBuilder();
        foreach (var r in (raw ?? "").EnumerateRunes())
        {
            if (Rune.IsControl(r) || r.Value is 0x200B or 0x200E or 0x200F or 0xFEFF) continue;
            sb.Append(r.ToString());
        }
        var s = sb.ToString().Trim();
        var info = new System.Globalization.StringInfo(s);
        if (info.LengthInTextElements > MaxNameChars) s = info.SubstringByTextElements(0, MaxNameChars).TrimEnd();
        return s.Length == 0 ? "Player" : s;
    }

    /// <summary>Sanitised name, deduplicated against the other players as "Name (2)", "Name (3)".</summary>
    public string UniqueName(string? raw, byte exceptSlot = 0xFF)
    {
        string baseName = SanitizeName(raw);
        bool Taken(string n) => _ordered.Any(p => p.Slot != exceptSlot && string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase));
        if (!Taken(baseName)) return baseName;
        for (int i = 2; ; i++)
        {
            string suffix = $" ({i})";
            var info = new System.Globalization.StringInfo(baseName);
            string stem = info.LengthInTextElements + suffix.Length > MaxNameChars
                ? info.SubstringByTextElements(0, Math.Max(1, MaxNameChars - suffix.Length))
                : baseName;
            string n = stem + suffix;
            if (!Taken(n)) return n;
        }
    }
}

/// <summary>The handshake check (spec 02 §5), pure. First failure wins; the detail names both values.</summary>
public static class HandshakeValidator
{
    public static (RejectCode Code, string Detail)? Validate(VersionInfo host, in Hello h, bool full, bool banned)
    {
        if (h.Proto != host.Proto) return (RejectCode.Protocol, $"Protocol {h.Proto}, host has {host.Proto}.");
        if (!string.Equals(h.ModVersion, host.ModVersion, StringComparison.Ordinal))
            return (RejectCode.ModVersion, $"Mod version {h.ModVersion}, host has {host.ModVersion}.");
        if (!string.Equals(h.GameVersion, host.GameVersion, StringComparison.Ordinal))
            return (RejectCode.GameBuild, $"Game version {h.GameVersion}, host has {host.GameVersion}.");
        if (h.SaveVersion != host.SaveVersion) return (RejectCode.SaveVersion, $"Save version {h.SaveVersion}, host has {host.SaveVersion}.");
        if (h.SteamBuildId != 0 && host.SteamBuildId != 0 && h.SteamBuildId != host.SteamBuildId)
            return (RejectCode.GameBuild, $"Steam build {h.SteamBuildId}, host has {host.SteamBuildId}.");
        if (h.ManifestHash != 0 && host.ManifestHash != 0 && h.ManifestHash != host.ManifestHash)
            return (RejectCode.Manifest, $"Scene manifest {h.ManifestHash:X8}, host has {host.ManifestHash:X8}.");
        if (banned) return (RejectCode.Kicked, "You were removed from this shift.");
        if (full) return (RejectCode.Full, "The shift is full.");
        return null;
    }
}
