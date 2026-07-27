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
        case "keys": return Keys(picPath);
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

        verify options:
          --pic <ic3>                  PIC key file (defaults to workspace ic3)
          --size N                     expected file size in bytes (default 67108864)
          --find "text"                confirm an edited string is present in the decrypted image

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
