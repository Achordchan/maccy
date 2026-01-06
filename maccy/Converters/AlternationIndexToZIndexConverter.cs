using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace maccy.Converters;

public sealed class AlternationIndexToZIndexConverter : IValueConverter
{
    public static readonly AlternationIndexToZIndexConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not int idx)
            return 0;

        return 100 - idx;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
