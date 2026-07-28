using System.Globalization;
using System.Windows.Data;

namespace M4Text.Editor;

/// <summary>Formats a long offset as 0x-prefixed 8-digit hex for display.</summary>
public sealed class HexConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
        => value is long l ? $"0x{l:x8}" : value?.ToString() ?? string.Empty;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// Presents control characters (newline/tab/carriage-return) as visible C-style
/// escapes so they can be viewed and typed inside single-line grid cells, while the
/// underlying model keeps the real bytes. This lets a translator inject a real line
/// break (<c>\n</c> = 0x0A) into game text to wrap a wide English line, without any
/// change to the byte-length accounting (a "\n" round-trips to a single 0x0A byte).
/// </summary>
public sealed class EscapeConverter : IValueConverter
{
    // model (raw string with control chars) -> view (visible escapes)
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
        => (value as string ?? string.Empty)
            .Replace("\\", "\\\\")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n")
            .Replace("\t", "\\t");

    // view (escapes) -> model (raw control chars). Order matters: unescape the
    // backslash last so "\\n" (literal backslash + n) is preserved.
    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        string s = value as string ?? string.Empty;
        var sb = new System.Text.StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                char n = s[++i];
                sb.Append(n switch { 'n' => '\n', 'r' => '\r', 't' => '\t', '\\' => '\\', _ => n });
            }
            else
            {
                sb.Append(s[i]);
            }
        }
        return sb.ToString();
    }
}

/// <summary>True/false -> Visible/Collapsed, for showing an "over limit" warning.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Non-null -> true; used to disable the detail editor when no row is selected.</summary>
public sealed class NotNullConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
        => value is not null;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

