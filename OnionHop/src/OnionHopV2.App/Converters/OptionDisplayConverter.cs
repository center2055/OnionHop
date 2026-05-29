using System;
using System.Globalization;
using System.Reflection;
using Avalonia.Data.Converters;

namespace OnionHopV2.App.Converters;

public sealed class OptionDisplayConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value == null)
        {
            return string.Empty;
        }

        var labelProperty = value.GetType().GetProperty("Label", BindingFlags.Public | BindingFlags.Instance);
        if (labelProperty?.GetValue(value) is string label && !string.IsNullOrWhiteSpace(label))
        {
            return label;
        }

        return value.ToString() ?? string.Empty;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
