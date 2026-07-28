using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace M4Text;

/// <summary>
/// ROM-free, human-editable changes file. Holds only the edits (and the hide-list)
/// so translation work can be committed to a repository without any original,
/// decrypted, or modified ROM bytes. Collaborators clone the repo, supply their own
/// ROM, and load this file to reproduce and extend the edits.
///
/// This is the single source of truth for the format, shared by the WPF editor and
/// the headless CLI (so CI can apply a repo's changes to any provided ROM).
/// </summary>
public sealed class M4TextPatch
{
    public string Format { get; set; } = "m4text-changes";
    public int Version { get; set; } = 1;
    public List<PatchEdit> Edits { get; set; } = new();
    public List<string> Hidden { get; set; } = new();

    // UnsafeRelaxedJsonEscaping keeps 日本語 / symbols readable in the committed file
    // instead of \uXXXX escapes; WriteIndented keeps diffs reviewable.
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Reads a changes file from disk. Throws on malformed/empty JSON.</summary>
    public static M4TextPatch Load(string path)
        => JsonSerializer.Deserialize<M4TextPatch>(File.ReadAllText(path), Json)
           ?? throw new InvalidDataException($"'{path}' is not a valid M4Text changes file.");

    /// <summary>Serializes to the canonical indented JSON string.</summary>
    public string Serialize() => JsonSerializer.Serialize(this, Json);

    /// <summary>Writes the changes file to disk in canonical form.</summary>
    public void Save(string path) => File.WriteAllText(path, Serialize());

    /// <summary>
    /// Builds a patch from a set of entries (only the modified ones are recorded)
    /// plus an optional hide-list. Ordered by file then offset for stable diffs.
    /// </summary>
    public static M4TextPatch FromEntries(IEnumerable<TextEntry> entries, IEnumerable<string>? hidden = null)
        => new()
        {
            Edits = entries.Where(e => e.IsModified)
                .OrderBy(e => e.File, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.Offset)
                .Select(e => new PatchEdit
                {
                    File = e.File,
                    Offset = $"0x{e.Offset:x}",
                    Encoding = e.Encoding,
                    Original = e.Original,
                    Text = e.Edited,
                })
                .ToList(),
            Hidden = (hidden ?? Enumerable.Empty<string>())
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };

    /// <summary>
    /// Applies this patch's edits onto the given entries, matching by (file, offset).
    /// The stored <see cref="PatchEdit.Original"/> is compared against each entry so
    /// version drift (a different ROM revision) is reported rather than silently
    /// mis-applied — edits are still applied so a close-enough ROM keeps working.
    /// </summary>
    public ApplyResult Apply(IEnumerable<TextEntry> entries)
    {
        var byKey = new Dictionary<(string File, long Offset), TextEntry>();
        foreach (var e in entries)
            byKey[(e.File, e.Offset)] = e; // offsets are unique per file; last wins is harmless

        int applied = 0, missing = 0, mismatched = 0;
        foreach (var ed in Edits)
        {
            if (!TryParseOffset(ed.Offset, out long off) ||
                !byKey.TryGetValue((ed.File, off), out var entry))
            {
                missing++;
                continue;
            }
            if (!string.IsNullOrEmpty(ed.Original) &&
                !string.Equals(ed.Original, entry.Original, StringComparison.Ordinal))
                mismatched++;
            entry.Edited = ed.Text ?? entry.Original;
            applied++;
        }
        return new ApplyResult(applied, missing, mismatched);
    }

    /// <summary>Parses an offset stored as hex ("0x24ed68") or plain hex ("24ed68").</summary>
    public static bool TryParseOffset(string? s, out long value)
    {
        value = 0;
        s = s?.Trim();
        if (string.IsNullOrEmpty(s)) return false;
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return long.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }
}

/// <summary>One edited slot. Offset is hex text (e.g. "0x24ed68"); Original is the
/// pristine slot text, kept only to validate against a collaborator's ROM.</summary>
public sealed class PatchEdit
{
    public string File { get; set; } = "";
    public string Offset { get; set; } = "";
    public string Encoding { get; set; } = "";
    public string Original { get; set; } = "";
    public string Text { get; set; } = "";
}

/// <summary>Outcome of applying a patch: how many edits landed, were not found, or
/// differed from the target ROM's pristine text.</summary>
public readonly record struct ApplyResult(int Applied, int Missing, int Mismatched);
