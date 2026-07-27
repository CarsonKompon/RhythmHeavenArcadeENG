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
