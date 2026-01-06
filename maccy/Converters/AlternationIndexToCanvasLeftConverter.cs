using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace maccy.Converters;

public sealed class AlternationIndexToCanvasLeftConverter : IValueConverter
{
    public static readonly AlternationIndexToCanvasLeftConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not int idx)
            return 0.0;

        return idx * 12.0;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
