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

/// <summary>
/// Turns a 0..1 fraction into a StrokeDashArray that draws that much of a circle's outline.
/// ConverterParameter = "diameter,thickness" (WPF dash lengths are measured in stroke thicknesses).
/// </summary>
public sealed class RingDashConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double fraction = value is double d ? Math.Clamp(d, 0, 1) : 0;
        var parts = (parameter as string ?? "20,2").Split(',');
        double diameter = double.Parse(parts[0], CultureInfo.InvariantCulture);
        double thickness = double.Parse(parts[1], CultureInfo.InvariantCulture);
        double circumference = Math.PI * (diameter - thickness);
        return new System.Windows.Media.DoubleCollection { Math.Max(0.001, fraction * circumference / thickness), 10000 };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Visible when the bound enum equals ConverterParameter.</summary>
public sealed class EnumVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value?.ToString() == parameter?.ToString() ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class InverseBoolToVisibility : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
