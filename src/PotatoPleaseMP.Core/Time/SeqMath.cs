namespace PotatoPleaseMP.Core.Time;

/// <summary>RFC 1982 serial-number arithmetic on 16-bit sequence numbers and stamps (spec 01 §6.2).</summary>
public static class SeqMath
{
    /// <summary>True when <paramref name="a"/> is strictly newer than <paramref name="b"/> (wrap-aware, half range).</summary>
    public static bool Newer(ushort a, ushort b) => a != b && (ushort)(a - b) < 0x8000;

    /// <summary>a - b as a signed distance in -32768..32767.</summary>
    public static int Diff(ushort a, ushort b) => (short)(ushort)(a - b);

    /// <summary>
    /// Unwraps a 16-bit stamp to the 32-bit value closest to <paramref name="reference"/>: valid within ±32768 ticks
    /// (±18 min at 30 Hz).
    /// </summary>
    public static uint Unwrap16(ushort stamp, uint reference)
    {
        int d = Diff(stamp, (ushort)reference);
        long v = (long)reference + d;
        if (v < 0) v += 0x10000; // before the start of time: take the next wrap
        return (uint)v;
    }

    /// <summary>True when 8-bit <paramref name="a"/> is newer than <paramref name="b"/> (pose seq, spec 04).</summary>
    public static bool Newer8(byte a, byte b) => (sbyte)(byte)(a - b) > 0;
}
