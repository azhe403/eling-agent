using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Eling.Desktop.Converters;

/// <summary>
/// A worktree row shows the branch glyph, a repository row the folder glyph —
/// the same two shapes the dashboard's workspace tree uses.
/// </summary>
public sealed class WorktreeIconConverter : IValueConverter
{
    public static readonly WorktreeIconConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "⑂" : "📁";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
