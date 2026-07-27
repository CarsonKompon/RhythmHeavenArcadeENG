using System.Text;

namespace M4Text;

public readonly record struct FoundString(long Offset, string Encoding, int ByteLength, string Text);

/// <summary>
/// Scans a decrypted ROM image for candidate human-readable strings. Supports
/// ASCII and Shift-JIS, since the game mixes English (bitmap font) and Japanese.
/// </summary>
public static class StringScanner
{
    private static bool IsPrintableAscii(byte b) => b >= 0x20 && b <= 0x7e;

    /// <summary>Finds runs of printable ASCII at least <paramref name="minLength"/> chars long.</summary>
    public static IEnumerable<FoundString> ScanAscii(byte[] data, int minLength)
    {
        int start = -1;
        for (int i = 0; i <= data.Length; i++)
        {
            bool printable = i < data.Length && IsPrintableAscii(data[i]);
            if (printable)
            {
                if (start < 0) start = i;
            }
            else if (start >= 0)
            {
                int len = i - start;
                if (len >= minLength)
                    yield return new FoundString(start, "ascii", len, Encoding.ASCII.GetString(data, start, len));
                start = -1;
            }
        }
    }

    private static bool IsSjisLead(byte b) => (b >= 0x81 && b <= 0x9f) || (b >= 0xe0 && b <= 0xfc);
    private static bool IsSjisTrail(byte b) => (b >= 0x40 && b <= 0x7e) || (b >= 0x80 && b <= 0xfc);
    private static bool IsSjisHalfKana(byte b) => b >= 0xa1 && b <= 0xdf;

    // Full-width kana are the strongest signal for real Japanese UI text (menus,
    // dialogue). Compressed/code noise rarely clusters in these ranges.
    // Hiragana: lead 0x82, trail 0x9f-0xf1. Katakana: lead 0x83, trail 0x40-0x96.
    private static bool IsKana(byte lead, byte trail)
        => (lead == 0x82 && trail >= 0x9f && trail <= 0xf1)
        || (lead == 0x83 && trail >= 0x40 && trail <= 0x96);

    /// <summary>
    /// Finds Shift-JIS runs. A run must contain at least <paramref name="minDoubleBytes"/>
    /// double-byte characters and at least <paramref name="minKana"/> full-width kana.
    /// The kana threshold is what cuts through compressed-data noise.
    /// </summary>
    public static IEnumerable<FoundString> ScanShiftJis(byte[] data, int minDoubleBytes, int minKana = 0)
    {
        var sjis = Encoding.GetEncoding(932);
        int i = 0;
        while (i < data.Length)
        {
            int start = i;
            int doubleByteCount = 0;
            int kanaCount = 0;
            int j = i;
            while (j < data.Length)
            {
                byte b = data[j];
                if (IsSjisLead(b) && j + 1 < data.Length && IsSjisTrail(data[j + 1]))
                {
                    if (IsKana(b, data[j + 1])) kanaCount++;
                    doubleByteCount++;
                    j += 2;
                }
                else if (IsPrintableAscii(b) || IsSjisHalfKana(b))
                {
                    j += 1;
                }
                else
                {
                    break;
                }
            }

            int len = j - start;
            if (doubleByteCount >= minDoubleBytes && kanaCount >= minKana && len > 0)
                yield return new FoundString(start, "sjis", len, sjis.GetString(data, start, len));

            i = j > start ? j : start + 1;
        }
    }

    // Codepoint is a Japanese script/punct character worth counting as "real text".
    private static bool IsJapaneseCodepoint(int cp)
        => (cp >= 0x3000 && cp <= 0x30ff)   // CJK punctuation, hiragana, katakana
        || (cp >= 0x4e00 && cp <= 0x9fff)   // CJK unified ideographs (kanji)
        || (cp >= 0xff00 && cp <= 0xffef);  // full/half-width forms (、。！？ etc.)

    // Decodes one UTF-8 codepoint at offset. Returns byte length (1-4) and the
    // codepoint, or 0 length if the sequence is not valid UTF-8.
    private static int DecodeUtf8(byte[] data, int i, out int cp)
    {
        cp = 0;
        byte b0 = data[i];
        if (b0 < 0x80) { cp = b0; return 1; }
        int len, min;
        if ((b0 & 0xe0) == 0xc0) { cp = b0 & 0x1f; len = 2; min = 0x80; }
        else if ((b0 & 0xf0) == 0xe0) { cp = b0 & 0x0f; len = 3; min = 0x800; }
        else if ((b0 & 0xf8) == 0xf0) { cp = b0 & 0x07; len = 4; min = 0x10000; }
        else return 0;

        if (i + len > data.Length) return 0;
        for (int k = 1; k < len; k++)
        {
            byte b = data[i + k];
            if ((b & 0xc0) != 0x80) return 0;
            cp = (cp << 6) | (b & 0x3f);
        }
        if (cp < min || cp > 0x10ffff || (cp >= 0xd800 && cp <= 0xdfff)) return 0; // overlong/invalid
        return len;
    }

    /// <summary>
    /// Finds valid UTF-8 runs (ASCII printable + multibyte) that contain at least
    /// <paramref name="minJapanese"/> Japanese characters. This is the game's actual
    /// script encoding.
    /// </summary>
    public static IEnumerable<FoundString> ScanUtf8(byte[] data, int minJapanese)
    {
        int i = 0;
        while (i < data.Length)
        {
            int start = i;
            int japaneseCount = 0;
            int j = i;
            while (j < data.Length)
            {
                byte b = data[j];
                if (b >= 0x20 && b <= 0x7e) { j++; continue; } // ASCII printable
                if (b < 0x80) break;                            // control byte -> end run

                int len = DecodeUtf8(data, j, out int cp);
                if (len == 0) break;
                if (IsJapaneseCodepoint(cp)) japaneseCount++;
                else if (cp > 0x7e && !IsJapaneseCodepoint(cp)) break; // non-JP unicode -> end run
                j += len;
            }

            int byteLen = j - start;
            if (japaneseCount >= minJapanese && byteLen > 0)
                yield return new FoundString(start, "utf8", byteLen, Encoding.UTF8.GetString(data, start, byteLen));

            i = j > start ? j : start + 1;
        }
    }
}
