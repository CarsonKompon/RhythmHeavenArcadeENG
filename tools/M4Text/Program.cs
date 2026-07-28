using System.Text;
using M4Text;

// Enable Shift-JIS (code page 932).
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

// Default PIC path for this workspace; overridable with --pic.
const string DefaultPic = @"E:\PROJECTS\Github\TengokuArcade\Original ROM\rhytngk\317-0503-jpn.ic3";

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

string command = args[0].ToLowerInvariant();
var rest = args.Skip(1).ToArray();
string picPath = GetOption(rest, "--pic") ?? DefaultPic;

try
{
    switch (command)
    {
        case "decrypt": return Decrypt(rest, picPath);
        case "encrypt": return Encrypt(rest, picPath);
        case "roundtrip": return Roundtrip(rest, picPath);
        case "verify": return Verify(rest, picPath);
        case "find": return Find(rest);
        case "map": return Map(rest);
        case "refs": return Refs(rest);
        case "xrefs": return XRefs(rest);
        case "textrefs": return TextRefs(rest);
        case "disasm": return Disasm(rest);
        case "keys": return Keys(picPath);
        case "apply": return Apply(rest, picPath);
        case "patch": return Patch(rest, picPath);
        default:
            Console.Error.WriteLine($"Unknown command: {command}");
            PrintUsage();
            return 1;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}

M4Codec LoadCodec(string pic) => new(File.ReadAllBytes(pic));

int Keys(string pic)
{
    var codec = LoadCodec(pic);
    Console.WriteLine($"subkey1 = 0x{codec.Subkey1:x4}");
    Console.WriteLine($"subkey2 = 0x{codec.Subkey2:x4}");
    return 0;
}

int Decrypt(string[] a, string pic)
{
    var (input, output) = TwoPaths(a, "decrypt");
    var codec = LoadCodec(pic);
    byte[] data = File.ReadAllBytes(input);
    codec.Decrypt(data);
    File.WriteAllBytes(output, data);
    Console.WriteLine($"Decrypted {data.Length:N0} bytes -> {output}");
    return 0;
}

int Encrypt(string[] a, string pic)
{
    var (input, output) = TwoPaths(a, "encrypt");
    var codec = LoadCodec(pic);
    byte[] data = File.ReadAllBytes(input);
    codec.Encrypt(data);
    File.WriteAllBytes(output, data);
    Console.WriteLine($"Encrypted {data.Length:N0} bytes -> {output}");
    return 0;
}

// Verifies the codec is a true inverse: decrypt then re-encrypt must reproduce
// the original bytes exactly. Proves round-trip integrity before any editing.
int Roundtrip(string[] a, string pic)
{
    string input = a.FirstOrDefault(x => !x.StartsWith("--"))
        ?? throw new ArgumentException("roundtrip requires an encrypted input file.");
    var codec = LoadCodec(pic);
    byte[] original = File.ReadAllBytes(input);
    byte[] work = (byte[])original.Clone();

    codec.Decrypt(work);
    codec.Encrypt(work);

    bool identical = work.AsSpan().SequenceEqual(original);
    Console.WriteLine(identical
        ? $"ROUND-TRIP OK: {input} ({original.Length:N0} bytes) decrypt->encrypt is byte-identical."
        : $"ROUND-TRIP FAILED: {input} differs after decrypt->encrypt.");
    return identical ? 0 : 2;
}

int Find(string[] a)
{
    string input = a.FirstOrDefault(x => !x.StartsWith("--"))
        ?? throw new ArgumentException("find requires a decrypted input file.");
    int minAscii = int.Parse(GetOption(a, "--min") ?? "4");
    int minKanji = int.Parse(GetOption(a, "--min-kanji") ?? "1");
    int minKana = int.Parse(GetOption(a, "--min-kana") ?? "0");
    int minJp = int.Parse(GetOption(a, "--min-jp") ?? "2");
    string enc = (GetOption(a, "--encoding") ?? "both").ToLowerInvariant();
    string? outPath = GetOption(a, "--out");

    byte[] data = File.ReadAllBytes(input);
    var results = new List<FoundString>();
    if (enc is "ascii" or "both") results.AddRange(StringScanner.ScanAscii(data, minAscii));
    if (enc is "utf8" or "both") results.AddRange(StringScanner.ScanUtf8(data, minJp));
    if (enc is "sjis") results.AddRange(StringScanner.ScanShiftJis(data, minKanji, minKana));
    results.Sort((x, y) => x.Offset.CompareTo(y.Offset));

    using TextWriter w = outPath is null ? Console.Out : new StreamWriter(outPath);
    foreach (var r in results)
        w.WriteLine($"0x{r.Offset:x8}\t{r.Encoding}\t{r.ByteLength}\t{Sanitize(r.Text)}");

    if (outPath is not null)
        Console.WriteLine($"Wrote {results.Count:N0} strings -> {outPath}");
    return 0;
}

// Prints the NAOMI transfer/load table (ROM<->RAM segments) from a decrypted
// ROM image, and optionally resolves an address in either direction.
int Map(string[] a)
{
    string input = a.FirstOrDefault(x => !x.StartsWith("--"))
        ?? throw new ArgumentException("map requires a decrypted ROM file.");
    byte[] rom = File.ReadAllBytes(input);
    var map = RomMemoryMap.Parse(rom);

    Console.WriteLine($"Transfer/load table ({map.Segments.Count} segment(s)):");
    Console.WriteLine("  romOffset   romEnd      ramAddr     ramEnd      length      rawField");
    foreach (var s in map.Segments)
        Console.WriteLine($"  0x{s.RomOffset:x8}  0x{s.RomEnd:x8}  0x{s.RamAddress:x8}  0x{s.RamEnd:x8}  0x{s.Length:x8}  0x{s.RawRomField:x8}");

    string? ro = GetOption(a, "--resolve");        // ROM offset -> RAM
    if (ro is not null)
    {
        long off = ParseNumber(ro);
        uint? ram = map.RomToRam(off);
        Console.WriteLine(ram is uint r
            ? $"ROM 0x{off:x8} -> RAM 0x{r:x8}"
            : $"ROM 0x{off:x8} -> (unmapped)");
    }

    string? ra = GetOption(a, "--resolve-ram");     // RAM -> ROM offset
    if (ra is not null)
    {
        uint addr = (uint)ParseNumber(ra);
        long? off = map.RamToRom(addr);
        Console.WriteLine(off is long o
            ? $"RAM 0x{addr:x8} -> ROM 0x{o:x8}"
            : $"RAM 0x{addr:x8} -> (unmapped)");
    }
    return 0;
}

// Finds every 32-bit pointer (literal-pool constant) that references a given target,
// specified as either a ROM offset (default) or a RAM address (--ram).
int Refs(string[] a)
{
    string input = a.FirstOrDefault(x => !x.StartsWith("--"))
        ?? throw new ArgumentException("refs requires a decrypted ROM file.");
    string? target = GetOption(a, "--target");
    if (target is null)
        throw new ArgumentException("refs requires --target <romOffset|ramAddr>.");

    byte[] rom = File.ReadAllBytes(input);
    var map = RomMemoryMap.Parse(rom);

    // Resolve the target to a RAM address (that's what pointers hold).
    uint ramAddr;
    if (GetOption(a, "--ram") is not null || target.StartsWith("0x0c", StringComparison.OrdinalIgnoreCase))
        ramAddr = (uint)ParseNumber(target);
    else
    {
        long off = ParseNumber(target);
        ramAddr = map.RomToRam(off) ?? throw new InvalidOperationException($"ROM 0x{off:x8} is not mapped to RAM.");
        Console.WriteLine($"Target ROM 0x{off:x8} -> RAM 0x{ramAddr:x8}");
    }

    var hits = PointerScanner.FindU32(rom, ramAddr);
    Console.WriteLine($"{hits.Count} pointer(s) to RAM 0x{ramAddr:x8}:");
    foreach (long h in hits)
    {
        uint? ptrRam = map.RomToRam(h);
        Console.WriteLine(ptrRam is uint pr
            ? $"  literal @ ROM 0x{h:x8}  (RAM 0x{pr:x8})"
            : $"  literal @ ROM 0x{h:x8}  (unmapped)");
    }
    return 0;
}

// Finds every 32-bit word pointing anywhere into a RAM address range. Unlike
// `refs` (exact target), this locates handlers that reference a table by an
// interior address, which is how we find the code that walks a menu table.
int XRefs(string[] a)
{
    string input = a.FirstOrDefault(x => !x.StartsWith("--"))
        ?? throw new ArgumentException("xrefs requires a decrypted ROM file.");
    string? fromS = GetOption(a, "--from");
    string? toS = GetOption(a, "--to");
    if (fromS is null || toS is null)
        throw new ArgumentException("xrefs requires --from <ramAddr> and --to <ramAddr>.");

    uint low = (uint)ParseNumber(fromS);
    uint high = (uint)ParseNumber(toS);
    int align = int.TryParse(GetOption(a, "--align"), out int al) ? al : 1;

    byte[] rom = File.ReadAllBytes(input);
    var map = RomMemoryMap.Parse(rom);

    var hits = PointerScanner.FindU32InRange(rom, low, high, align);
    Console.WriteLine($"{hits.Count} word(s) pointing into RAM [0x{low:x8}, 0x{high:x8}):");
    foreach (var h in hits)
    {
        uint? ptrRam = map.RomToRam(h.Offset);
        string site = ptrRam is uint pr ? $"RAM 0x{pr:x8}" : "unmapped";
        Console.WriteLine($"  @ ROM 0x{h.Offset:x8} ({site}) -> 0x{h.Value:x8}");
    }
    return 0;
}

// Enumerates every 32-bit pointer in the mapped image that targets the text
// region, resolving each to its on-screen string. This is the data backbone for a
// layout/display-list view: each hit is a place where a string is referenced, and
// the words immediately around the pointer are candidate layout fields (X/Y/scale).
int TextRefs(string[] a)
{
    string input = a.FirstOrDefault(x => !x.StartsWith("--"))
        ?? throw new ArgumentException("textrefs requires a decrypted ROM file.");
    // Text region to match against (ROM offsets). Defaults cover the rhytngk script block.
    long from = ParseNumber(GetOption(a, "--from") ?? "0x230000");
    long to = ParseNumber(GetOption(a, "--to") ?? "0x260000");
    int minLen = int.TryParse(GetOption(a, "--min"), out int ml) ? ml : 2;
    // Optional: dump N u32 words on each side of the pointer, classified as int/ptr.
    int context = int.TryParse(GetOption(a, "--context"), out int cx) ? cx : 0;
    string? filter = GetOption(a, "--contains");

    byte[] rom = File.ReadAllBytes(input);
    var map = RomMemoryMap.Parse(rom);

    int found = 0;
    foreach (var r in TextReferenceScanner.Scan(rom, map, from, to, minLen, filter, context))
    {
        Console.WriteLine($"ptr @ ROM 0x{r.PointerRom:x8} (RAM 0x{r.PointerRam:x8}) -> str ROM 0x{r.StringRom:x8} (RAM 0x{r.StringRam:x8})  \"{Sanitize(r.Text)}\"");
        foreach (var f in r.Context)
        {
            string cls = f.TargetRom is long wr ? $"ptr->ROM 0x{wr:x8}" : $"int {(int)f.Value}";
            string mark = f.RelOffset == 0 ? " <== textPtr" : "";
            Console.WriteLine($"    +{f.RelOffset,4}  0x{f.Rom:x8}: 0x{f.Value:x8}  {cls}{mark}");
        }
        found++;
    }
    Console.WriteLine($"{found} text pointer(s) in [0x{from:x}, 0x{to:x}).");
    return 0;
}

// Parses a decimal or 0x-prefixed hex number.
static long ParseNumber(string s)
{
    s = s.Trim();
    return s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? Convert.ToInt64(s[2..], 16)
        : long.Parse(s);
}

// Disassembles SH-4 code from a ROM offset (default) or RAM address (--ram).
int Disasm(string[] a)
{
    string input = a.FirstOrDefault(x => !x.StartsWith("--"))
        ?? throw new ArgumentException("disasm requires a decrypted ROM file.");
    string? at = GetOption(a, "--at");
    if (at is null)
        throw new ArgumentException("disasm requires --at <romOffset|ramAddr>.");
    int count = int.TryParse(GetOption(a, "--count"), out int c) ? c : 32;

    byte[] rom = File.ReadAllBytes(input);
    var map = RomMemoryMap.Parse(rom);

    long romOff;
    if (GetOption(a, "--ram") is not null || at.StartsWith("0x0c", StringComparison.OrdinalIgnoreCase))
        romOff = map.RamToRom((uint)ParseNumber(at)) ?? throw new InvalidOperationException("RAM address is not mapped.");
    else
        romOff = ParseNumber(at);

    var dis = new Sh4Disassembler(rom, map);
    Console.WriteLine("  romOffset   ramAddr   bytes  mnemonic");
    foreach (string line in dis.Disassemble(romOff, count))
        Console.WriteLine(line);
    return 0;
}

static string Sanitize(string s)
{
    var sb = new StringBuilder(s.Length);
    foreach (char c in s)
        sb.Append(c is '\t' or '\r' or '\n' ? ' ' : c);
    return sb.ToString();
}

// Sanity-checks an *encrypted* ROM file so a bad export is caught before it ever
// reaches Flycast. Verifies file size, decrypts in-memory, checks the plaintext
// header magic ("NAOMI"), and optionally confirms an edited string is present.
int Verify(string[] a, string pic)
{
    string input = a.FirstOrDefault(x => !x.StartsWith("--"))
        ?? throw new ArgumentException("verify requires an encrypted ROM file.");
    string? find = GetOption(a, "--find");
    long expectSize = long.TryParse(GetOption(a, "--size"), out long sz) ? sz : 67_108_864L;

    byte[] rom = File.ReadAllBytes(input);
    Console.WriteLine($"File: {input}");
    Console.WriteLine($"Size: {rom.Length:N0} bytes"
        + (rom.Length == expectSize ? "  (OK)" : $"  MISMATCH: expected {expectSize:N0}"));

    var codec = LoadCodec(pic);
    byte[] plain = (byte[])rom.Clone();
    codec.Decrypt(plain);

    // NAOMI cart header magic lives at offset 0 of the decrypted image.
    string header = Encoding.ASCII.GetString(plain, 0, Math.Min(64, plain.Length));
    int nul = header.IndexOf('\0');
    if (nul >= 0) header = header[..nul];
    bool naomi = header.StartsWith("NAOMI", StringComparison.Ordinal);
    Console.WriteLine($"Header: \"{Sanitize(header)}\"  {(naomi ? "(NAOMI OK)" : "MISSING 'NAOMI' MAGIC")}");

    if (find is not null)
    {
        byte[] needle = Encoding.UTF8.GetBytes(find);
        int hits = 0;
        for (int i = 0; i + needle.Length <= plain.Length; i++)
        {
            if (plain.AsSpan(i, needle.Length).SequenceEqual(needle))
            {
                Console.WriteLine($"  found \"{find}\" at 0x{i:x8}");
                if (++hits >= 10) { Console.WriteLine("  ...(more)"); break; }
            }
        }
        if (hits == 0) Console.WriteLine($"  \"{find}\" NOT FOUND in decrypted image");
    }

    return naomi && rom.Length == expectSize ? 0 : 2;
}

// Applies a ROM-free changes file to an original ROM set and emits a BPS binary patch
// per changed ROM file. This is the CI target: it never leaves a full ROM on disk —
// only the tiny .bps files, which are safe to publish (they contain just our deltas).
int Patch(string[] a, string picDefault)
{
    string romDir = GetOption(a, "--rom")
        ?? throw new ArgumentException("patch requires --rom <dir> (folder with the fpr-*.ic* set + PIC).");
    string changesPath = GetOption(a, "--changes")
        ?? throw new ArgumentException("patch requires --changes <file>.");
    string outDir = GetOption(a, "--out")
        ?? throw new ArgumentException("patch requires --out <dir>.");

    string pic = GetOption(a, "--pic")
        ?? new[] { Path.Combine(romDir, "317-0503-jpn.ic3") }.FirstOrDefault(File.Exists)
        ?? picDefault;

    PadMode pad = (GetOption(a, "--pad") ?? "auto").ToLowerInvariant() switch
    {
        "null" => PadMode.Null,
        "space" => PadMode.Space,
        _ => PadMode.Auto,
    };

    string? pinnedWork = GetOption(a, "--work");
    string work = pinnedWork ?? Path.Combine(Path.GetTempPath(), "m4text-patch-" + Guid.NewGuid().ToString("n"));
    bool cleanup = pinnedWork is null;

    try
    {
        Directory.CreateDirectory(work);
        var svc = RomTextService.Load(work, pic, romDir);
        var entries = svc.GetEntries(forceRescan: true);

        var patch = M4TextPatch.Load(changesPath);
        var result = patch.Apply(entries);
        int written = svc.ApplyEdits(entries, pad);                 // throws on any over-limit edit

        Directory.CreateDirectory(outDir);
        var modified = svc.ExportEncryptedToMemory(pic);            // encrypted, in memory only
        var madePatches = new List<string>();

        foreach (var (romName, targetBytes) in modified)
        {
            string origPath = Path.Combine(romDir, romName);
            if (!File.Exists(origPath))
            {
                Console.Error.WriteLine($"  ! original '{romName}' not found in --rom; skipping its patch.");
                continue;
            }
            byte[] sourceBytes = File.ReadAllBytes(origPath);
            byte[] bps = BpsPatch.Create(sourceBytes, targetBytes);

            // Self-check: apply the patch we just built back onto the original and confirm
            // it reproduces the target exactly, so a broken patch can never be published.
            byte[] roundTrip = BpsPatch.Apply(sourceBytes, bps);
            if (!roundTrip.AsSpan().SequenceEqual(targetBytes))
                throw new InvalidOperationException($"BPS self-check failed for {romName}; patch not written.");

            string bpsPath = Path.Combine(outDir, romName + ".bps");
            File.WriteAllBytes(bpsPath, bps);
            madePatches.Add(bpsPath);
        }

        Console.WriteLine($"Applied {result.Applied} edit(s) from {Path.GetFileName(changesPath)} "
            + $"({written} slot(s)); wrote {madePatches.Count} BPS patch(es).");
        if (result.Missing > 0)
            Console.WriteLine($"  {result.Missing} edit(s) had no matching slot in this ROM (skipped).");
        if (result.Mismatched > 0)
            Console.WriteLine($"  {result.Mismatched} edit(s) differ from this ROM's original text (applied anyway; check ROM version).");
        foreach (var p in madePatches)
            Console.WriteLine($"  -> {p} ({new FileInfo(p).Length:N0} bytes)");

        return madePatches.Count > 0 ? 0 : 3;
    }
    finally
    {
        if (cleanup && Directory.Exists(work))
        {
            try { Directory.Delete(work, recursive: true); } catch { /* scratch dir; ignore */ }
        }
    }
}

// Applies a ROM-free changes file (changes.m4text.json) to an original ROM set and
// writes the re-encrypted, modified ROM files. This is the headless equivalent of the
// editor's Load-changes + Export-ROM, so CI can rebuild patch inputs with no GUI.
int Apply(string[] a, string picDefault)
{
    string romDir = GetOption(a, "--rom")
        ?? throw new ArgumentException("apply requires --rom <dir> (folder with the fpr-*.ic* set + PIC).");
    string changesPath = GetOption(a, "--changes")
        ?? throw new ArgumentException("apply requires --changes <file>.");
    string outDir = GetOption(a, "--out")
        ?? throw new ArgumentException("apply requires --out <dir>.");

    // Prefer an explicit --pic, else the PIC shipped alongside the ROM set, else the
    // workspace default. CI bundles the PIC with the ic files, so the middle case wins.
    string pic = GetOption(a, "--pic")
        ?? new[] { Path.Combine(romDir, "317-0503-jpn.ic3") }.FirstOrDefault(File.Exists)
        ?? picDefault;

    PadMode pad = (GetOption(a, "--pad") ?? "auto").ToLowerInvariant() switch
    {
        "null" => PadMode.Null,
        "space" => PadMode.Space,
        _ => PadMode.Auto,
    };

    // Decrypt into a scratch work folder so the ROM dir stays pristine (never gets
    // *.dec spilled into it). Reused/persisted only when --work pins a location.
    string? pinnedWork = GetOption(a, "--work");
    string work = pinnedWork ?? Path.Combine(Path.GetTempPath(), "m4text-apply-" + Guid.NewGuid().ToString("n"));
    bool cleanup = pinnedWork is null;

    try
    {
        Directory.CreateDirectory(work); // must exist before the .dec scan enumerates it
        var svc = RomTextService.Load(work, pic, romDir);
        // Fresh scan gives pristine Original text to validate the patch against.
        var entries = svc.GetEntries(forceRescan: true);

        var patch = M4TextPatch.Load(changesPath);
        var result = patch.Apply(entries);
        int written = svc.ApplyEdits(entries, pad);           // throws on any over-limit edit
        var files = svc.ExportEncrypted(pic, outDir);

        Console.WriteLine($"Applied {result.Applied} edit(s) from {Path.GetFileName(changesPath)} "
            + $"({written} slot(s) written across {files.Count} file(s)).");
        if (result.Missing > 0)
            Console.WriteLine($"  {result.Missing} edit(s) had no matching slot in this ROM (skipped).");
        if (result.Mismatched > 0)
            Console.WriteLine($"  {result.Mismatched} edit(s) differ from this ROM's original text (applied anyway; check ROM version).");
        foreach (var f in files)
            Console.WriteLine($"  -> {f}");

        // No modified files means nothing to patch: signal so CI can skip empty output.
        return files.Count > 0 ? 0 : 3;
    }
    finally
    {
        if (cleanup && Directory.Exists(work))
        {
            try { Directory.Delete(work, recursive: true); } catch { /* scratch dir; ignore */ }
        }
    }
}

static (string input, string output) TwoPaths(string[] a, string cmd)
{
    var positional = a.Where(x => !x.StartsWith("--")).ToArray();
    if (positional.Length < 2)
        throw new ArgumentException($"{cmd} requires <input> <output>.");
    return (positional[0], positional[1]);
}

static string? GetOption(string[] a, string name)
{
    int idx = Array.FindIndex(a, x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
    return idx >= 0 && idx + 1 < a.Length ? a[idx + 1] : null;
}

static void PrintUsage()
{
    Console.WriteLine("""
        m4text - Sega NAOMI M4 ROM text codec/scanner for rhytngk

        Usage:
          m4text keys                                 Show subkeys derived from the PIC
          m4text decrypt   <in> <out> [--pic <ic3>]   Decrypt an encrypted ROM image
          m4text encrypt   <in> <out> [--pic <ic3>]   Re-encrypt a plaintext ROM image
          m4text roundtrip <in>       [--pic <ic3>]   Verify decrypt->encrypt is byte-identical
          m4text verify    <rom.ic8>  [options]      Validate an exported ROM (size/header/string)
          m4text find      <decrypted> [options]      List candidate strings (offset/enc/len/text)
          m4text map       <decrypted> [options]      Print NAOMI ROM<->RAM transfer table
          m4text refs      <decrypted> --target <a>   Find pointers to a ROM offset or RAM address
          m4text textrefs  <decrypted> [options]      List every text pointer + target string (+record)
          m4text disasm    <decrypted> --at <a>       Disassemble SH-4 code at an offset/address
          m4text apply     --rom <dir> --changes <f> --out <dir>   Apply a changes file -> re-encrypted ROM
          m4text patch     --rom <dir> --changes <f> --out <dir>   Apply changes -> BPS patch per changed file

        map options:
          --resolve <romOffset>        Resolve a ROM offset to its RAM address
          --resolve-ram <ramAddr>      Resolve a RAM address to its ROM offset

        refs options:
          --target <romOffset|ramAddr> Target to find pointers to (required)
          --ram                        Treat --target as a RAM address (else ROM offset)

        textrefs options:
          --from <romOffset>           Start of text region to match (default 0x230000)
          --to <romOffset>             End of text region to match (default 0x260000)
          --min N                      Minimum target string length (default 2)
          --contains "text"            Only show references whose string contains text
          --context N                  Dump N u32 words each side of the pointer (record fields)

        disasm options:
          --at <romOffset|ramAddr>     Where to start disassembling (required)
          --count N                    Instruction count (default 32)
          --ram                        Treat --at as a RAM address (else ROM offset)

        verify options:
          --pic <ic3>                  PIC key file (defaults to workspace ic3)
          --size N                     expected file size in bytes (default 67108864)
          --find "text"                confirm an edited string is present in the decrypted image

        apply options:
          --rom <dir>                  folder holding the original fpr-*.ic* set (+ PIC) (required)
          --changes <file>             ROM-free changes file (changes.m4text.json) (required)
          --out <dir>                  where the re-encrypted, modified ROM files are written (required)
          --pic <ic3>                  PIC key (default: <rom>/317-0503-jpn.ic3, else workspace ic3)
          --pad auto|null|space        padding for shorter replacements (default auto)
          --work <dir>                 reuse/persist the scratch decrypt folder (default: temp, deleted)

        patch options:                 (same as apply; output is BPS patches instead of ROM files)
          --rom <dir>                  folder holding the original fpr-*.ic* set (+ PIC) (required)
          --changes <file>             ROM-free changes file (changes.m4text.json) (required)
          --out <dir>                  where <romfile>.bps patches are written (required)
          --pic <ic3>                  PIC key (default: <rom>/317-0503-jpn.ic3, else workspace ic3)
          --pad auto|null|space        padding for shorter replacements (default auto)
          --work <dir>                 reuse/persist the scratch decrypt folder (default: temp, deleted)

        find options:
          --encoding ascii|utf8|sjis|both   (default both = ascii+utf8)
          --min N                      min ASCII run length (default 4)
          --min-jp N                   min Japanese chars for a UTF-8 run (default 2)
          --min-kanji N                min double-byte chars for an SJIS run (default 1)
          --min-kana N                 min full-width kana for an SJIS run (default 0; cuts noise)
          --out <file>                 write TSV to file instead of stdout

        --pic defaults to the workspace 317-0503-jpn.ic3.
        """);
}
