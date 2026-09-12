using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Eling.Desktop.Converters;

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
