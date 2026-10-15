using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Unbounded.Panorama.Engine;

namespace Unbounded.Panorama.App;

public sealed class BoolToVisibility : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = value switch
        {
            bool b => b,
            null => false,
            string s => s.Length > 0,
            _ => true,
        };
        return visible ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class EngineStateBrush : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        EngineState.Connected => new SolidColorBrush(Color.FromRgb(0x3D, 0xDC, 0x97)),
        EngineState.Connecting or EngineState.Starting => new SolidColorBrush(Color.FromRgb(0xFF, 0xC2, 0x47)),
        EngineState.BlockedByKanali => new SolidColorBrush(Color.FromRgb(0xFF, 0x7A, 0x59)),
        _ => new SolidColorBrush(Color.FromRgb(0x7C, 0x87, 0x99)),
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Badges get horizontal padding inside the pill, like the cooler draws them.</summary>
public sealed class BadgePadding : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is [bool badge, int size] && badge)
            return new Thickness(size * 0.35, size * 0.05, size * 0.35, size * 0.08);
        return new Thickness(0);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>True when the bound value equals the parameter; ConvertBack returns the parameter when checked.</summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? parameter! : Binding.DoNothing;
}

/// <summary>Visible when the bound value equals the parameter (page switching).</summary>
public sealed class EqualsToVisibility : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class EnumLabel : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        Layout.WidgetContent.CpuName => "CPU name",
        Layout.WidgetContent.GpuName => "GPU name",
        Layout.BackgroundFit.Fill => "Fill (crop)",
        Layout.BackgroundFit.Fit => "Fit (letterbox)",
        Layout.Orientation.Portrait => "Portrait (vertical)",
        Layout.Orientation.Landscape => "Landscape (horizontal)",
        null => "",
        _ => value.ToString() ?? "",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
