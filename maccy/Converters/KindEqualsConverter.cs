using System;
using System.Globalization;
using Avalonia.Data.Converters;
using maccy.Models;

namespace maccy.Converters;

public sealed class KindEqualsConverter : IValueConverter
{
    public static readonly KindEqualsConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not ClipboardContentKind kind)
            return false;

        if (parameter is null)
            return false;

        if (parameter is ClipboardContentKind pKind)
            return kind == pKind;

        if (parameter is string s && Enum.TryParse<ClipboardContentKind>(s, ignoreCase: true, out var parsed))
            return kind == parsed;

        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
