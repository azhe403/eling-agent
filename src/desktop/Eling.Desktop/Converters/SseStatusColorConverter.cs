using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Eling.Desktop.Converters;

public sealed class SseStatusColorConverter : IValueConverter
{
    public static readonly SseStatusColorConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value?.ToString()?.ToLowerInvariant() switch
        {
            "connected" => Colors.Green,
            "connecting" => Colors.Orange,
            _ => Colors.Red
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
