using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace maccy.Converters;

public sealed class NotNullOrEmptyToBoolConverter : IValueConverter
{
    public static readonly NotNullOrEmptyToBoolConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is string s && !string.IsNullOrEmpty(s);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
