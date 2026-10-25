using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Eling.Desktop.Converters;

/// <summary>
/// Maps a bool to a row background brush: the selected brush (from
/// <c>ConverterParameter</c>, defaulting to the sidebar accent) when true,
/// otherwise transparent. Used by the memory sidebar so a ticked workspace is
/// tinted, not just check-marked.
/// </summary>
public sealed class BoolToRowBrushConverter : IValueConverter
{
    public static BoolToRowBrushConverter Instance { get; } = new();

    private static readonly IBrush Selected = new SolidColorBrush(Color.Parse("#2b4a72"));
    private static readonly IBrush None = Brushes.Transparent;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not true) return None;
        if (parameter is string text && !string.IsNullOrWhiteSpace(text))
        {
            return new SolidColorBrush(Color.Parse(text));
        }
        return Selected;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
