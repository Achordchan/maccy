using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Data.Converters;

namespace maccy.Converters;

public sealed class FilePathsToMultilineStringConverter : IValueConverter
{
    public static readonly FilePathsToMultilineStringConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not IEnumerable<string> paths)
            return null;

        var filtered = paths.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        if (filtered.Length == 0)
            return null;

        return string.Join(Environment.NewLine, filtered);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
