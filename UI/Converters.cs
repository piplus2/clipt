using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Clipt.UI;

/// <summary>
/// WPF's Track sizes the thumb as track × viewport / (range + viewport), with only a ~8px floor, so in a long
/// list the thumb becomes a dot. Inflating the viewport the Track sees guarantees a minimum thumb length;
/// Track maps thumb position back to the full range, so dragging still covers the whole list.
/// Inputs: ViewportSize, Minimum, Maximum, track length (ActualHeight/ActualWidth). Parameter: min length.
/// </summary>
public sealed class MinThumbViewport : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 4 || values.Any(v => v is not double)) return values.FirstOrDefault(v => v is double) ?? 0d;
        double viewport = (double)values[0], min = (double)values[1], max = (double)values[2], track = (double)values[3];
        double minThumb = double.TryParse(parameter as string, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : 36;
        double range = max - min;
        if (range <= 0 || track <= minThumb * 1.5 || double.IsNaN(viewport)) return viewport;
        double needed = minThumb * range / (track - minThumb);
        return Math.Max(viewport, needed);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class BoolToVisibility : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool b = value switch
        {
            bool x => x,
            string s => !string.IsNullOrEmpty(s),
            int i => i != 0,
            null => false,
            _ => true,
        };
        if (Invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
