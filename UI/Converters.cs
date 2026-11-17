using System.Globalization;
using Avalonia.Data.Converters;
using FluentIcons.Common;

namespace Clipt.UI;

/// <summary>Turns a Fluent icon name (as models expose it) into the icon enum.</summary>
public sealed class IconNameConverter : IValueConverter
{
    public static readonly IconNameConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string name && Enum.TryParse<Icon>(name, out var icon) ? icon : Icon.Document;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
