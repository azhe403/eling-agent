using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Eling.Desktop.Converters;

/// <summary>
/// Fills the active segment of a small segmented control — the workspace
/// grouping switch. A neutral pill, not the accent colour: the dashboard's
/// grouping switch reads as "which arrangement", not as a primary action.
/// </summary>
public sealed class SegmentButtonConverter : IValueConverter
{
    public static readonly SegmentButtonConverter Instance = new();

    private static readonly SolidColorBrush Active = new(Color.Parse("#3f3f46"));
    private static readonly SolidColorBrush Inactive = new(Colors.Transparent);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var current = value?.ToString() ?? "";
        var tag = (parameter as string) ?? "";
        return string.Equals(current, tag, StringComparison.OrdinalIgnoreCase) ? Active : Inactive;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
