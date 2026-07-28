using System.Buffers.Binary;

namespace M4Text;

/// <summary>
/// Minimal, deterministic writer for the BPS binary patch format (the modern
/// romhacking standard, applied by Floating IPS / beat). We only ever need to
/// diff two same-length ROM images that differ in a handful of slots, so the
/// encoder emits just two action kinds:
///   * SourceRead  — copy an unchanged run straight from the player's ROM
///   * TargetRead  — embed a changed run's literal bytes
/// That keeps patches tiny (only our edits) yet fully spec-compliant, and the
/// embedded source/target/patch CRC32s let the patcher reject the wrong ROM.
/// </summary>
public static class BpsPatch
{
    /// <summary>Builds a BPS patch that transforms <paramref name="source"/> into
    /// <paramref name="target"/>. Both are typically the encrypted ROM file.</summary>
    public static byte[] Create(byte[] source, byte[] target)
    {
        using var ms = new MemoryStream(target.Length / 8 + 64);

        ms.WriteByte((byte)'B');
        ms.WriteByte((byte)'P');
        ms.WriteByte((byte)'S');
        ms.WriteByte((byte)'1');
        WriteNumber(ms, (ulong)source.Length);
        WriteNumber(ms, (ulong)target.Length);
        WriteNumber(ms, 0); // no metadata

        int pos = 0;
        while (pos < target.Length)
        {
            bool matches = pos < source.Length && source[pos] == target[pos];
            int run = 1;
            if (matches)
            {
                while (pos + run < target.Length && pos + run < source.Length &&
                       source[pos + run] == target[pos + run])
                    run++;
                // SourceRead (command 0): copy `run` bytes from source at this offset.
                WriteNumber(ms, ((ulong)(run - 1) << 2) | 0);
            }
            else
            {
                while (pos + run < target.Length &&
                       !(pos + run < source.Length && source[pos + run] == target[pos + run]))
                    run++;
                // TargetRead (command 1): the next `run` literal bytes follow inline.
                WriteNumber(ms, ((ulong)(run - 1) << 2) | 1);
                ms.Write(target, pos, run);
            }
            pos += run;
        }

        // Footer: source CRC, target CRC, then CRC of everything written so far.
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(crc, Crc32(source));
        ms.Write(crc);
        BinaryPrimitives.WriteUInt32LittleEndian(crc, Crc32(target));
        ms.Write(crc);

        byte[] body = ms.ToArray();
        uint patchCrc = Crc32(body);
        var result = new byte[body.Length + 4];
        Array.Copy(body, result, body.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(body.Length), patchCrc);
        return result;
    }

    // BPS variable-length number: 7 bits per byte, little-endian, high bit marks the
    // final byte; each continuation implicitly subtracts one to avoid redundant zeros.
    private static void WriteNumber(Stream s, ulong n)
    {
        while (true)
        {
            byte x = (byte)(n & 0x7f);
            n >>= 7;
            if (n == 0)
            {
                s.WriteByte((byte)(0x80 | x));
                break;
            }
            s.WriteByte(x);
            n--;
        }
    }

    /// <summary>
    /// Applies a BPS patch to <paramref name="source"/> and returns the target,
    /// validating the source/target CRC32s embedded in the patch. Implements all four
    /// BPS actions so it can verify any conforming patch, not just ones we emit.
    /// Throws <see cref="InvalidDataException"/> on any malformed input or CRC mismatch.
    /// </summary>
    public static byte[] Apply(byte[] source, byte[] patch)
    {
        if (patch.Length < 4 + 12 ||
            patch[0] != 'B' || patch[1] != 'P' || patch[2] != 'S' || patch[3] != '1')
            throw new InvalidDataException("Not a BPS patch (bad magic).");

        // Verify the patch's own integrity before trusting its contents.
        uint patchCrcStored = BinaryPrimitives.ReadUInt32LittleEndian(patch.AsSpan(patch.Length - 4));
        if (Crc32(patch.AsSpan(0, patch.Length - 4)) != patchCrcStored)
            throw new InvalidDataException("BPS patch is corrupt (patch CRC mismatch).");

        int p = 4;
        ulong sourceSize = ReadNumber(patch, ref p);
        ulong targetSize = ReadNumber(patch, ref p);
        ulong metaSize = ReadNumber(patch, ref p);
        p += (int)metaSize; // skip metadata

        if ((ulong)source.Length != sourceSize)
            throw new InvalidDataException($"Source size {source.Length} != patch's expected {sourceSize} (wrong ROM).");

        var target = new byte[targetSize];
        int outputOffset = 0, sourceRel = 0, targetRel = 0;
        int actionsEnd = patch.Length - 12; // footer = 3 x u32

        while (p < actionsEnd)
        {
            ulong data = ReadNumber(patch, ref p);
            long length = (long)(data >> 2) + 1;
            switch (data & 3)
            {
                case 0: // SourceRead
                    for (long i = 0; i < length; i++, outputOffset++)
                        target[outputOffset] = source[outputOffset];
                    break;
                case 1: // TargetRead
                    for (long i = 0; i < length; i++)
                        target[outputOffset++] = patch[p++];
                    break;
                case 2: // SourceCopy
                    sourceRel += ReadSigned(patch, ref p);
                    for (long i = 0; i < length; i++)
                        target[outputOffset++] = source[sourceRel++];
                    break;
                default: // 3 TargetCopy
                    targetRel += ReadSigned(patch, ref p);
                    for (long i = 0; i < length; i++)
                        target[outputOffset++] = target[targetRel++];
                    break;
            }
        }

        uint srcCrc = BinaryPrimitives.ReadUInt32LittleEndian(patch.AsSpan(actionsEnd));
        uint tgtCrc = BinaryPrimitives.ReadUInt32LittleEndian(patch.AsSpan(actionsEnd + 4));
        if (Crc32(source) != srcCrc)
            throw new InvalidDataException("Source ROM does not match the patch (source CRC mismatch).");
        if (Crc32(target) != tgtCrc)
            throw new InvalidDataException("Patched output is wrong (target CRC mismatch).");
        return target;
    }

    private static ulong ReadNumber(byte[] data, ref int p)
    {
        ulong result = 0;
        int shift = 0;
        while (true)
        {
            byte x = data[p++];
            result += (ulong)(x & 0x7f) << shift;
            if ((x & 0x80) != 0) break;
            shift += 7;
            result += 1ul << shift;
        }
        return result;
    }

    private static int ReadSigned(byte[] data, ref int p)
    {
        ulong n = ReadNumber(data, ref p);
        int mag = (int)(n >> 1);
        return (n & 1) != 0 ? -mag : mag;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    /// <summary>Standard zlib/PNG CRC-32 (the checksum BPS uses).</summary>
    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }
}
