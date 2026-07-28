namespace M4Text;

/// <summary>
/// Finds pointer references inside a decrypted ROM image. On SH-4, a 32-bit
/// address is materialised as a constant in a PC-relative literal pool
/// (<c>mov.l @(disp,PC),Rn</c>), so a pointer to a string is simply that string's
/// RAM address stored little-endian somewhere in the loaded code/data.
/// </summary>
public static class PointerScanner
{
    /// <summary>
    /// Returns the byte offsets of every little-endian 32-bit occurrence of
    /// <paramref name="value"/> in <paramref name="data"/>. When <paramref name="value"/>
    /// is a code RAM address, these offsets are the literal-pool constants (i.e. the
    /// pointers) that must be rewritten to relocate the target.
    /// </summary>
    public static List<long> FindU32(ReadOnlySpan<byte> data, uint value)
    {
        var hits = new List<long>();
        byte b0 = (byte)value, b1 = (byte)(value >> 8), b2 = (byte)(value >> 16), b3 = (byte)(value >> 24);
        int end = data.Length - 4;
        for (int i = 0; i <= end; i++)
        {
            if (data[i] == b0 && data[i + 1] == b1 && data[i + 2] == b2 && data[i + 3] == b3)
                hits.Add(i);
        }
        return hits;
    }

    public readonly record struct RangeHit(long Offset, uint Value);

    /// <summary>
    /// Returns every little-endian 32-bit word whose value falls within
    /// [<paramref name="lowInclusive"/>, <paramref name="highExclusive"/>). Used to
    /// find code/data that points anywhere into a table (not just its exact base),
    /// which is how we locate the handler that consumes a menu-definition table.
    /// </summary>
    public static List<RangeHit> FindU32InRange(ReadOnlySpan<byte> data, uint lowInclusive, uint highExclusive, int align = 1)
    {
        var hits = new List<RangeHit>();
        if (align < 1) align = 1;
        int end = data.Length - 4;
        for (int i = 0; i <= end; i += align)
        {
            uint v = (uint)(data[i] | (data[i + 1] << 8) | (data[i + 2] << 16) | (data[i + 3] << 24));
            if (v >= lowInclusive && v < highExclusive)
                hits.Add(new RangeHit(i, v));
        }
        return hits;
    }
}
