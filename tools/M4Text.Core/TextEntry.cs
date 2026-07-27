using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;

namespace M4Text;

/// <summary>How to fill the leftover bytes when a replacement is shorter than the original slot.</summary>
public enum PadMode
{
    /// <summary>Space (0x20) if the original ended with a space, otherwise null (0x00).</summary>
    Auto,
    Null,
    Space,
}

/// <summary>
/// A single editable string located in a decrypted ROM file. The byte slot it
/// occupies is fixed (<see cref="MaxBytes"/>); replacements may never exceed it,
/// which keeps every edit safe to write back in place.
/// </summary>
public sealed class TextEntry : INotifyPropertyChanged
{
    public string File { get; }        // logical file key, e.g. "ic8"
    public long Offset { get; }
    public string Encoding { get; }    // "ascii" | "utf8"
    public string Original { get; }
    public int MaxBytes { get; }       // original byte length; hard cap for edits

    private string _edited;

    // <paramref name="current"/> lets the loader seed the live value from the
    // current (possibly edited) ROM bytes while keeping <paramref name="original"/>
    // as the pristine text from the persisted index — so a saved edit never gets
    // mistaken for the original on reload.
    public TextEntry(string file, long offset, string encoding, string original, int maxBytes, string? current = null)
    {
        File = file;
        Offset = offset;
        Encoding = encoding;
        Original = original;
        MaxBytes = maxBytes;
        _edited = current ?? original;
    }

    public string Edited
    {
        get => _edited;
        set
        {
            if (_edited == value) return;
            _edited = value ?? string.Empty;
            OnPropertyChanged();
            OnPropertyChanged(nameof(EditedByteLength));
            OnPropertyChanged(nameof(RemainingBytes));
            OnPropertyChanged(nameof(IsModified));
            OnPropertyChanged(nameof(IsOverLimit));
        }
    }

    public System.Text.Encoding TextEncoding => Encoding == "utf8"
        ? System.Text.Encoding.UTF8
        : System.Text.Encoding.ASCII;

    public int EditedByteLength => TextEncoding.GetByteCount(_edited);
    public int RemainingBytes => MaxBytes - EditedByteLength;
    public bool IsModified => !string.Equals(_edited, Original, StringComparison.Ordinal);
    public bool IsOverLimit => EditedByteLength > MaxBytes;

    /// <summary>
    /// Encodes the edited text and pads to exactly <see cref="MaxBytes"/> so the
    /// slot is fully overwritten (no stale tail from the original string).
    /// </summary>
    public byte[] BuildSlotBytes(PadMode padMode)
    {
        byte[] encoded = TextEncoding.GetBytes(_edited);
        if (encoded.Length > MaxBytes)
            throw new InvalidOperationException($"Edit at 0x{Offset:x8} is {encoded.Length} bytes, exceeds slot {MaxBytes}.");

        byte pad = padMode switch
        {
            PadMode.Null => 0x00,
            PadMode.Space => 0x20,
            _ => Original.EndsWith(' ') ? (byte)0x20 : (byte)0x00,
        };

        var slot = new byte[MaxBytes];
        Array.Copy(encoded, slot, encoded.Length);
        for (int i = encoded.Length; i < MaxBytes; i++)
            slot[i] = pad;
        return slot;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
