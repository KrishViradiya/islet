using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Islet.Core;

/// <summary>Binds a RadioButton to an enum value: IsChecked="{Binding Theme, Converter=..., ConverterParameter=Dark}".</summary>
public sealed class EnumBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value?.ToString() == parameter?.ToString();

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter != null
            ? (targetType.IsEnum ? Enum.Parse(targetType, parameter.ToString()!) : parameter)
            : Binding.DoNothing;
}

/// <summary>Formats slider values: ConverterParameter = pct | px | ms.</summary>
public sealed class UnitConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double v = System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
        return (parameter as string) switch
        {
            "pct" => $"{v * 100:0}%",
            "px" => $"{v:0} px",
            "ms" => $"{v:0} ms",
            _ => v.ToString("0.##", culture),
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class InverseBoolToVisibility : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
