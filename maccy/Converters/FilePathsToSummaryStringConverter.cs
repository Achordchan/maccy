using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia.Data.Converters;

namespace maccy.Converters;

public sealed class FilePathsToSummaryStringConverter : IValueConverter
{
    public static readonly FilePathsToSummaryStringConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not IEnumerable<string> paths)
            return null;

        var arr = paths.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        if (arr.Length == 0)
            return null;

        var firstName = Path.GetFileName(arr[0]);
        if (string.IsNullOrWhiteSpace(firstName))
            firstName = arr[0];

        if (arr.Length == 1)
            return firstName;

        return $"{firstName} (+{arr.Length - 1})";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
