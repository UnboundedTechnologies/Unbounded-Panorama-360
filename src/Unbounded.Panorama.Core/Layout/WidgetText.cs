using System.Globalization;
using Unbounded.Panorama.Sensors;

namespace Unbounded.Panorama.Layout;

/// <summary>Resolves the text a widget shows right now.</summary>
public sealed class WidgetText(ISensorReadings sensors)
{
    public string Resolve(Widget w, bool fahrenheit)
    {
        var body = w.Content switch
        {
            WidgetContent.Text => w.Text,
            WidgetContent.Sensor => sensors.Format(w.SensorId, w.Decimals, w.ShowUnit, fahrenheit),
            WidgetContent.Clock => SafeFormat(w.Format, "HH:mm"),
            WidgetContent.Date => SafeFormat(w.Format, "dddd d MMMM"),
            WidgetContent.CpuName => sensors.CpuName,
            WidgetContent.GpuName => sensors.GpuName,
            _ => "",
        };
        return w.Content == WidgetContent.Text ? body : w.Prefix + body + w.Suffix;
    }

    private static string SafeFormat(string? format, string fallback)
    {
        try
        {
            return DateTime.Now.ToString(string.IsNullOrWhiteSpace(format) ? fallback : format, CultureInfo.CurrentCulture);
        }
        catch (FormatException)
        {
            return DateTime.Now.ToString(fallback, CultureInfo.CurrentCulture);
        }
    }
}
