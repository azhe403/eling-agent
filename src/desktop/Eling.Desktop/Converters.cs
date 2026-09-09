using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Eling.Desktop;

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

public sealed class ScopeButtonConverter : IValueConverter
{
    public static readonly ScopeButtonConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var scope = value?.ToString() ?? "";
        var tag = (parameter as string) ?? "";
        var isActive = string.Equals(scope, tag, StringComparison.OrdinalIgnoreCase);
        return new SolidColorBrush(isActive ? Color.Parse("#3b82f6") : Color.Parse("#262626"));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class ProjectNameConverter : IValueConverter
{
    public static readonly ProjectNameConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var path = value?.ToString() ?? "";
        var name = System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
        return string.IsNullOrEmpty(name) ? path : $"📁 {name}";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
