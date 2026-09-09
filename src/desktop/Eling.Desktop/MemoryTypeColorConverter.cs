using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Eling.Desktop;

public sealed class MemoryTypeColorConverter : IValueConverter
{
    public static readonly MemoryTypeColorConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value?.ToString()?.ToLowerInvariant() switch
        {
            "fact" => new SolidColorBrush(Color.Parse("#1e3a5f")),
            "preference" => new SolidColorBrush(Color.Parse("#3d1a45")),
            "decision" => new SolidColorBrush(Color.Parse("#472807")),
            "lesson" => new SolidColorBrush(Color.Parse("#143d22")),
            "note" => new SolidColorBrush(Color.Parse("#2e2e2e")),
            _ => new SolidColorBrush(Color.Parse("#252525"))
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
