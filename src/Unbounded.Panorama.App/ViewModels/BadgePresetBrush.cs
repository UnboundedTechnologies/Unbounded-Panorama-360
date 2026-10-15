using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Unbounded.Panorama.App.ViewModels;

/// <summary>Renders a (From, To) badge preset as its gradient swatch.</summary>
public sealed class BadgePresetBrush : IValueConverter
{
    public static readonly BadgePresetBrush Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is ValueTuple<string, string> preset
            ? new LinearGradientBrush(WidgetViewModel.ColorOf(preset.Item1), WidgetViewModel.ColorOf(preset.Item2), 0)
            : Brushes.Transparent;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
