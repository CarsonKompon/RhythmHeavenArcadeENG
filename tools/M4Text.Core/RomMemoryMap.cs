using System.Buffers.Binary;

namespace M4Text;

/// <summary>
/// Reconstructs the ROM↔RAM address mapping used by the NAOMI boot loader.
///
/// The cart header carries a transfer/load table (at <see cref="TransferListOffset"/>):
/// a list of 12-byte entries — { uint32 romField, uint32 ramAddress, uint32 length } —
/// terminated by a romField of 0xFFFFFFFF. The BIOS DMAs <c>length</c> bytes from the
/// ROM into SH-4 RAM (0x0c000000 space), so game code references data by RAM address,
/// not ROM offset. This map lets us translate between the two.
///
/// The romField's high bits are flags (this cart uses 0x40000000); the actual ROM
/// offset is the low 29 bits.
/// </summary>
public sealed class RomMemoryMap
{
    public const int TransferListOffset = 0x360;
    private const uint Terminator = 0xFFFFFFFF;

    // The low 29 bits hold the ROM offset; the top bits are loader flags (e.g. 0x40000000).
    private const uint RomOffsetMask = 0x1FFFFFFF;

    public sealed record Segment(long RomOffset, uint RamAddress, int Length, uint RawRomField)
    {
        public long RomEnd => RomOffset + Length;
        public uint RamEnd => (uint)(RamAddress + (uint)Length);
        public bool ContainsRom(long off) => off >= RomOffset && off < RomEnd;
        public bool ContainsRam(uint addr) => addr >= RamAddress && addr < RamEnd;
    }

    public IReadOnlyList<Segment> Segments { get; }

    private RomMemoryMap(IReadOnlyList<Segment> segments) => Segments = segments;

    /// <summary>Parses the transfer/load table from a decrypted ROM image.</summary>
    public static RomMemoryMap Parse(ReadOnlySpan<byte> rom, int listOffset = TransferListOffset)
    {
        var segments = new List<Segment>();
        for (int p = listOffset; p + 12 <= rom.Length; p += 12)
        {
            uint romField = BinaryPrimitives.ReadUInt32LittleEndian(rom.Slice(p, 4));
            if (romField == Terminator) break;

            uint ram = BinaryPrimitives.ReadUInt32LittleEndian(rom.Slice(p + 4, 4));
            uint len = BinaryPrimitives.ReadUInt32LittleEndian(rom.Slice(p + 8, 4));

            // A zero-length or clearly bogus entry means we've run past the real list.
            if (len == 0 || len > rom.Length) break;

            long romOffset = romField & RomOffsetMask;
            segments.Add(new Segment(romOffset, ram, (int)len, romField));
        }
        return new RomMemoryMap(segments);
    }

    /// <summary>ROM file offset → SH-4 RAM address, or null if unmapped.</summary>
    public uint? RomToRam(long romOffset)
    {
        foreach (var s in Segments)
            if (s.ContainsRom(romOffset))
                return (uint)(s.RamAddress + (uint)(romOffset - s.RomOffset));
        return null;
    }

    /// <summary>SH-4 RAM address → ROM file offset, or null if unmapped.</summary>
    public long? RamToRom(uint ramAddress)
    {
        foreach (var s in Segments)
            if (s.ContainsRam(ramAddress))
                return s.RomOffset + (ramAddress - s.RamAddress);
        return null;
    }
}
