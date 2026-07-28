using System.Text;

namespace M4Text;

/// <summary>
/// One 32-bit word adjacent to (or at) a text pointer inside its command record.
/// The interpreter treats records as {argCount, funcPtr, arg} triples, so the
/// neighbouring words are the candidate layout fields (position/scale/opcode).
/// </summary>
/// <param name="RelOffset">Byte offset relative to the text pointer (0 == the pointer itself).</param>
/// <param name="Rom">Absolute ROM offset of this word.</param>
/// <param name="Value">The little-endian 32-bit value stored here.</param>
/// <param name="TargetRom">If <paramref name="Value"/> is a mapped RAM pointer, the ROM offset it targets; else null.</param>
public readonly record struct RecordField(int RelOffset, long Rom, uint Value, long? TargetRom)
{
    public bool IsPointer => TargetRom is not null;
}

/// <summary>
/// A single place where an on-screen string is referenced by a 32-bit pointer,
/// together with the surrounding command-record fields.
/// </summary>
public sealed record TextReference(
    long PointerRom,
    uint PointerRam,
    long StringRom,
    uint StringRam,
    string Text,
    IReadOnlyList<RecordField> Context);

/// <summary>
/// Enumerates every pointer in the mapped image that targets a text region and
/// resolves it to its on-screen string. This is the shared data engine behind the
/// console <c>textrefs</c> command and the editor's layout/references view.
/// </summary>
public static class TextReferenceScanner
{
    /// <summary>
    /// Scans all mapped segments for 4-aligned little-endian pointers whose target
    /// RAM address falls inside the ROM range <c>[from, to)</c>, decoding each target
    /// as a NUL-terminated UTF-8 string.
    /// </summary>
    /// <param name="rom">Decrypted ROM image.</param>
    /// <param name="map">Reconstructed ROM↔RAM map.</param>
    /// <param name="from">Inclusive start of the text region (ROM offset).</param>
    /// <param name="to">Exclusive end of the text region (ROM offset).</param>
    /// <param name="minLen">Minimum decoded string length to report (filters stray matches).</param>
    /// <param name="contains">Optional case-insensitive substring the text must contain.</param>
    /// <param name="context">Number of u32 words to capture on each side of the pointer (0 = just the pointer).</param>
    /// <param name="maxStringBytes">Upper bound on how many bytes to read while decoding a target string.</param>
    public static IEnumerable<TextReference> Scan(
        byte[] rom,
        RomMemoryMap map,
        long from,
        long to,
        int minLen = 2,
        string? contains = null,
        int context = 0,
        int maxStringBytes = 96)
    {
        // Resolve the text region to the RAM window that pointers will hold. Both
        // endpoints must be mapped; callers pass offsets inside the loaded segment.
        if (map.RomToRam(from) is not uint ramFrom || map.RomToRam(to - 1) is not uint ramTo)
            yield break;

        foreach (var seg in map.Segments)
        {
            long start = (seg.RomOffset + 3) & ~3L; // literal pools / tables are 4-aligned
            long segEnd = Math.Min(seg.RomEnd, rom.Length);
            for (long p = start; p + 4 <= segEnd; p += 4)
            {
                uint v = ReadU32(rom, p);
                if (v < ramFrom || v > ramTo) continue;
                if (map.RamToRom(v) is not long targetRom) continue;

                string text = ReadCString(rom, targetRom, maxStringBytes);
                if (text.Length < minLen) continue;
                if (contains is not null && !text.Contains(contains, StringComparison.OrdinalIgnoreCase)) continue;

                var fields = context > 0
                    ? BuildContext(rom, map, seg, p, context)
                    : Array.Empty<RecordField>();

                yield return new TextReference(
                    PointerRom: p,
                    PointerRam: map.RomToRam(p) ?? 0,
                    StringRom: targetRom,
                    StringRam: v,
                    Text: text,
                    Context: fields);
            }
        }
    }

    private static RecordField[] BuildContext(byte[] rom, RomMemoryMap map, RomMemoryMap.Segment seg, long ptr, int context)
    {
        var list = new List<RecordField>(context * 2 + 1);
        for (int k = -context; k <= context; k++)
        {
            long wo = ptr + k * 4;
            if (wo < seg.RomOffset || wo + 4 > seg.RomEnd) continue;
            uint w = ReadU32(rom, wo);
            list.Add(new RecordField(k * 4, wo, w, map.RamToRom(w)));
        }
        return list.ToArray();
    }

    private static uint ReadU32(byte[] rom, long off) =>
        (uint)(rom[off] | (rom[off + 1] << 8) | (rom[off + 2] << 16) | (rom[off + 3] << 24));

    /// <summary>Reads a NUL-terminated UTF-8 string at a ROM offset, bounded by <paramref name="maxBytes"/>.</summary>
    public static string ReadCString(byte[] rom, long off, int maxBytes)
    {
        if (off < 0 || off >= rom.Length) return string.Empty;
        int startOff = (int)off;
        int end = startOff;
        int limit = (int)Math.Min(rom.Length, off + maxBytes);
        while (end < limit && rom[end] != 0) end++;
        return Encoding.UTF8.GetString(rom, startOff, end - startOff);
    }
}
