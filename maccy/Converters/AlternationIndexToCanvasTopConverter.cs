using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace maccy.Converters;

public sealed class AlternationIndexToCanvasTopConverter : IValueConverter
{
    public static readonly AlternationIndexToCanvasTopConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not int idx)
            return 0.0;

        return idx * 10.0;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
