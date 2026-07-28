using System.Text;
using System.Text.Json;

namespace M4Text;

/// <summary>
/// Loads decrypted ROM files (*.dec) from a work folder, exposes their strings
/// as editable <see cref="TextEntry"/> items, and writes edits back — both as
/// updated plaintext (.dec) and as re-encrypted ROM files for the emulator.
/// </summary>
public sealed class RomTextService
{
    // Persisted catalog of discovered slots. It is the source of truth for each
    // slot's pristine Original text, so edits saved into the .dec files are never
    // mistaken for the original on a later reload, and relaunching skips the
    // (expensive) full rescan.
    public sealed record SlotRecord(string File, long Offset, string Encoding, string Original, int MaxBytes);

    // Maps a decrypted work file (by base name) to its encrypted ROM filename.
    private static readonly Dictionary<string, string> DecToRom = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ic8"] = "fpr-24423.ic8",
        ["ic9"] = "fpr-24424.ic9",
        ["ic10"] = "fpr-24425.ic10",
        ["ic11"] = "fpr-24426.ic11",
    };

    private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _modifiedFiles = new(StringComparer.OrdinalIgnoreCase);

    public string WorkFolder { get; }
    public IReadOnlyCollection<string> FileNames => _files.Keys;
    public IReadOnlyCollection<string> ModifiedFiles => _modifiedFiles;

    // Persisted index lives beside the .dec files; hidden from the *.dec glob.
    private string IndexPath => Path.Combine(WorkFolder, ".m4text-index.json");

    private RomTextService(string workFolder) => WorkFolder = workFolder;

    /// <summary>
    /// Loads every *.dec file in the folder. When none are present and a PIC key
    /// is supplied, the encrypted ROM set (searched in the work folder, the optional
    /// <paramref name="romFolder"/>, and the PIC's own folder) is decrypted into
    /// *.dec first, so an empty work folder bootstraps itself instead of failing.
    /// </summary>
    public static RomTextService Load(string workFolder, string? picPath = null, string? romFolder = null)
    {
        var svc = new RomTextService(workFolder);
        svc.LoadDecFiles();

        if (svc._files.Count == 0 && !string.IsNullOrWhiteSpace(picPath))
            svc.DecryptFromRoms(picPath!, romFolder);

        if (svc._files.Count == 0)
            throw new InvalidOperationException(
                $"No .dec files found in '{workFolder}', and no encrypted ROM set could be located to decrypt. " +
                "Put the ROM files (or their .dec) in the work folder, or point the PIC at the ROM set.");
        return svc;
    }

    private void LoadDecFiles()
    {
        foreach (string path in Directory.EnumerateFiles(WorkFolder, "*.dec"))
            _files[Path.GetFileNameWithoutExtension(path)] = File.ReadAllBytes(path);
    }

    // Decrypts any known encrypted ROM file it can find into memory and writes a
    // matching pristine .dec so subsequent launches load instantly.
    private void DecryptFromRoms(string picPath, string? romFolder = null)
    {
        string picDir = Path.GetDirectoryName(Path.GetFullPath(picPath)) ?? WorkFolder;
        string[] searchDirs = new[] { WorkFolder, romFolder, picDir }
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(d => d!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        M4Codec? codec = null;
        Directory.CreateDirectory(WorkFolder);

        foreach (var (decName, romName) in DecToRom)
        {
            string? romPath = searchDirs
                .Select(d => Path.Combine(d, romName))
                .FirstOrDefault(File.Exists);
            if (romPath is null) continue;

            codec ??= new M4Codec(File.ReadAllBytes(picPath));
            byte[] data = File.ReadAllBytes(romPath);
            codec.Decrypt(data);
            _files[decName] = data;
            File.WriteAllBytes(Path.Combine(WorkFolder, decName + ".dec"), data);
        }
    }

    /// <summary>
    /// Returns the editable entries. Uses the persisted index when present (fast,
    /// and keeps pristine Original text); otherwise performs a full scan and
    /// writes the index. Pass <paramref name="forceRescan"/> to rebuild it.
    /// </summary>
    public List<TextEntry> GetEntries(int minAscii = 4, int minJapanese = 2, bool forceRescan = false)
    {
        if (!forceRescan && TryLoadIndex() is { Count: > 0 } catalog)
            return BuildFromCatalog(catalog);

        var entries = Scan(minAscii, minJapanese);
        SaveIndex(entries);
        return entries;
    }

    // Rebuilds entries from the persisted catalog: Original comes from the index
    // (pristine), while the live value is decoded from the current .dec bytes.
    private List<TextEntry> BuildFromCatalog(List<SlotRecord> catalog)
    {
        var list = new List<TextEntry>(catalog.Count);
        foreach (var r in catalog)
        {
            if (!_files.TryGetValue(r.File, out var data)) continue;
            if (r.Offset < 0 || r.Offset + r.MaxBytes > data.Length) continue;
            string current = DecodeSlot(data, (int)r.Offset, r.MaxBytes, r.Encoding);
            list.Add(new TextEntry(r.File, r.Offset, r.Encoding, r.Original, r.MaxBytes, current));
        }
        list.Sort((a, b) =>
        {
            int c = string.CompareOrdinal(a.File, b.File);
            return c != 0 ? c : a.Offset.CompareTo(b.Offset);
        });
        return list;
    }

    // Decodes a fixed slot, trimming trailing NUL pad bytes written by BuildSlotBytes.
    // Interior line breaks (0x0A) are preserved (decoded as \n) so a multi-line message
    // round-trips; only trailing NUL padding is stripped.
    private static string DecodeSlot(byte[] data, int offset, int len, string encoding)
    {
        int realLen = len;
        while (realLen > 0 && data[offset + realLen - 1] == 0) realLen--;
        var enc = encoding == "utf8" ? Encoding.UTF8 : Encoding.ASCII;
        return enc.GetString(data, offset, realLen);
    }

    private List<SlotRecord>? TryLoadIndex()
    {
        try
        {
            if (!File.Exists(IndexPath)) return null;
            return JsonSerializer.Deserialize<List<SlotRecord>>(File.ReadAllText(IndexPath));
        }
        catch
        {
            return null; // Corrupt/old index -> fall back to a fresh scan.
        }
    }

    private void SaveIndex(IEnumerable<TextEntry> entries)
    {
        var records = entries
            .Select(e => new SlotRecord(e.File, e.Offset, e.Encoding, e.Original, e.MaxBytes))
            .ToList();
        File.WriteAllText(IndexPath, JsonSerializer.Serialize(records));
    }

    /// <summary>Scans all loaded files for ASCII and UTF-8 (Japanese) strings.</summary>
    public List<TextEntry> Scan(int minAscii = 4, int minJapanese = 2)
    {
        var list = new List<TextEntry>();
        foreach (var (name, data) in _files)
        {
            // One unified pass keeps each multi-line message (ASCII, Japanese, or mixed
            // with the odd full-width glyph) as a single entry, so editing preserves its
            // line breaks instead of fragmenting it.
            foreach (var s in StringScanner.ScanMessages(data, minAscii, minJapanese))
                list.Add(new TextEntry(name, s.Offset, s.Encoding, s.Text, s.ByteLength));
        }
        list.Sort((a, b) =>
        {
            int c = string.CompareOrdinal(a.File, b.File);
            return c != 0 ? c : a.Offset.CompareTo(b.Offset);
        });
        return list;
    }

    /// <summary>
    /// Writes every modified entry into the in-memory plaintext buffers.
    /// Throws if any edit exceeds its slot. Returns the number of slots written.
    /// </summary>
    public int ApplyEdits(IEnumerable<TextEntry> entries, PadMode padMode)
    {
        // Validate first so we never half-apply.
        var modified = entries.Where(e => e.IsModified).ToList();
        var overflow = modified.Where(e => e.IsOverLimit).ToList();
        if (overflow.Count > 0)
            throw new InvalidOperationException(
                $"{overflow.Count} edit(s) exceed their byte slot; fix before saving (e.g. 0x{overflow[0].Offset:x8}).");

        int count = 0;
        foreach (var e in modified)
        {
            if (!_files.TryGetValue(e.File, out var data)) continue;
            byte[] slot = e.BuildSlotBytes(padMode);
            Array.Copy(slot, 0, data, e.Offset, slot.Length);
            _modifiedFiles.Add(e.File);
            count++;
        }
        return count;
    }

    /// <summary>
    /// Returns the live in-memory plaintext buffer for a decrypted file (e.g. "ic8"),
    /// or null if it is not loaded. Callers must not mutate the returned array
    /// except through <see cref="PatchDword"/> so modified-file tracking stays correct.
    /// </summary>
    public byte[]? GetFileBytes(string decName)
        => _files.TryGetValue(decName, out var data) ? data : null;

    /// <summary>
    /// Writes a little-endian 32-bit value at <paramref name="offset"/> in the given
    /// decrypted file and flags it modified so it is included in SaveDec/Export.
    /// Used by the Layout tab to edit display-list record fields (positions/sizes)
    /// directly. Throws if the file is missing or the offset is out of range.
    /// </summary>
    public void PatchDword(string decName, long offset, uint value)
    {
        if (!_files.TryGetValue(decName, out var data))
            throw new InvalidOperationException($"File '{decName}' is not loaded.");
        if (offset < 0 || offset + 4 > data.Length)
            throw new ArgumentOutOfRangeException(nameof(offset), $"Offset 0x{offset:x} is outside '{decName}'.");
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan((int)offset, 4), value);
        _modifiedFiles.Add(decName);
    }

    /// <summary>
    /// Overwrites a byte range in the given decrypted file and flags it modified.
    /// Used to write a whole record string slot (multi-line message + terminator +
    /// NUL padding) in one shot. Throws if the file is missing or the range is out
    /// of bounds.
    /// </summary>
    public void PatchBytes(string decName, long offset, ReadOnlySpan<byte> data)
    {
        if (!_files.TryGetValue(decName, out var buf))
            throw new InvalidOperationException($"File '{decName}' is not loaded.");
        if (offset < 0 || offset + data.Length > buf.Length)
            throw new ArgumentOutOfRangeException(nameof(offset), $"Range at 0x{offset:x} is outside '{decName}'.");
        data.CopyTo(buf.AsSpan((int)offset, data.Length));
        _modifiedFiles.Add(decName);
    }

    /// <summary>Persists the (edited) plaintext buffers back to the *.dec files.</summary>
    public void SaveDec()
    {
        foreach (var (name, data) in _files)
            File.WriteAllBytes(Path.Combine(WorkFolder, name + ".dec"), data);
    }

    /// <summary>
    /// Re-encrypts the modified plaintext buffers with the M4 key from
    /// <paramref name="picPath"/> and returns them keyed by encrypted ROM filename
    /// (e.g. "fpr-24423.ic8"). Only files with applied edits are included. Nothing
    /// is written to disk — used by the patch builder to diff in memory.
    /// </summary>
    public IReadOnlyDictionary<string, byte[]> ExportEncryptedToMemory(string picPath)
    {
        var codec = new M4Codec(File.ReadAllBytes(picPath));
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in _modifiedFiles)
        {
            if (!_files.TryGetValue(name, out var data)) continue;
            string romName = DecToRom.TryGetValue(name, out var rn) ? rn : name + ".bin";
            var enc = (byte[])data.Clone();
            codec.Encrypt(enc);
            result[romName] = enc;
        }
        return result;
    }

    /// <summary>
    /// Re-encrypts the modified plaintext buffers with the M4 key from
    /// <paramref name="picPath"/> and writes the encrypted ROM files into
    /// <paramref name="outFolder"/>. Only files with applied edits are exported.
    /// </summary>
    public IReadOnlyList<string> ExportEncrypted(string picPath, string outFolder)
    {
        Directory.CreateDirectory(outFolder);
        var written = new List<string>();
        foreach (var (romName, enc) in ExportEncryptedToMemory(picPath))
        {
            string outPath = Path.Combine(outFolder, romName);
            File.WriteAllBytes(outPath, enc);
            written.Add(outPath);
        }
        return written;
    }
}
