using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Eling.Desktop.Converters;

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
